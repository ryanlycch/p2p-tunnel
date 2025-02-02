﻿using client.messengers.clients;
using client.messengers.punchHole;
using client.messengers.punchHole.udp;
using client.messengers.singnin;
using client.realize.messengers.crypto;
using common.libs;
using common.libs.extends;
using common.server;
using common.server.model;
using common.server.servers.rudp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;

namespace client.realize.messengers.punchHole.udp
{
    /// <summary>
    /// rudp打洞
    /// </summary>
    public sealed class PunchHoleRUdpMessengerSender : IPunchHoleUdp
    {
        private readonly PunchHoleMessengerSender punchHoleMessengerSender;
        private readonly SignInStateInfo signInState;
        private readonly CryptoSwap cryptoSwap;
        private readonly Config config;
        private readonly WheelTimer<object> wheelTimer;
        private readonly IClientInfoCaching clientInfoCaching;
        private readonly IClientsTunnel clientsTunnel;

        public PunchHoleRUdpMessengerSender(PunchHoleMessengerSender punchHoleMessengerSender, SignInStateInfo signInState, CryptoSwap cryptoSwap, Config config, WheelTimer<object> wheelTimer, IClientInfoCaching clientInfoCaching, IClientsTunnel clientsTunnel)
        {
            this.punchHoleMessengerSender = punchHoleMessengerSender;
            this.signInState = signInState;
            this.cryptoSwap = cryptoSwap;
            this.config = config;
            this.wheelTimer = wheelTimer;
            this.clientInfoCaching = clientInfoCaching;
            this.clientsTunnel = clientsTunnel;
        }
        private IConnection Connection => signInState.Connection;
#if DEBUG
        private bool UseLocalPort = true;
#else
        private bool UseLocalPort = true;
#endif

        private readonly ConcurrentDictionary<ulong, ConnectCacheModel> connectCache = new();
        public event IPunchHoleUdp.StepEvent OnStepHandler;

        public async Task<ConnectResultModel> Send(ConnectParams param)
        {
            param.LocalPort = await clientsTunnel.NewBind(ServerType.UDP, Connection.ConnectId, param.Id);

            TaskCompletionSource<ConnectResultModel> tcs = new TaskCompletionSource<ConnectResultModel>();
            connectCache.TryAdd(param.Id, new ConnectCacheModel
            {
                Tcs = tcs,
                LocalPort = param.LocalPort,
            });
            AddSendTimeout(param.Id);

            await punchHoleMessengerSender.Request(new SendPunchHoleArg<PunchHoleStep1Info>
            {
                Connection = Connection,
                ToId = param.Id,
                NewTunnel = param.NewTunnel,
                Data = new PunchHoleStep1Info { Step = (byte)PunchHoleUdpSteps.STEP_1, PunchType = PunchHoleTypes.UDP }
            }).ConfigureAwait(false);
            return await tcs.Task.ConfigureAwait(false);
        }
        public async Task InputData(PunchHoleStepModel model)
        {
            PunchHoleUdpSteps step = (PunchHoleUdpSteps)model.RawData.PunchStep;

            RemoveSendTimeout(model.RawData.FromId);
            OnStepHandler?.Invoke(this, model);
            switch (step)
            {
                case PunchHoleUdpSteps.STEP_1:
                    {
                        PunchHoleNotifyInfo data = new PunchHoleNotifyInfo();
                        data.DeBytes(model.RawData.Data);
                        model.Data = data;
                        _ = OnStep1(model);
                    }
                    break;
                case PunchHoleUdpSteps.STEP_2:
                    {
                        PunchHoleNotifyInfo data = new PunchHoleNotifyInfo();
                        data.DeBytes(model.RawData.Data);
                        model.Data = data;
                        _ = OnStep2(model);
                    }
                    break;
                case PunchHoleUdpSteps.STEP_2_1:
                    {
                        PunchHoleNotifyInfo data = new PunchHoleNotifyInfo();
                        data.DeBytes(model.RawData.Data);
                        model.Data = data;
                        _ = OnStep21(model);
                    }
                    break;
                case PunchHoleUdpSteps.STEP_2_Fail:
                    {
                        OnStep2Fail(model);
                    }
                    break;
                case PunchHoleUdpSteps.STEP_3:
                    {
                        PunchHoleStep3Info data = new PunchHoleStep3Info();
                        data.DeBytes(model.RawData.Data);
                        model.Data = data;
                        _ = OnStep3(model);
                    }
                    break;
                case PunchHoleUdpSteps.STEP_4:
                    {
                        PunchHoleStep4Info data = new PunchHoleStep4Info();
                        data.DeBytes(model.RawData.Data);
                        model.Data = data;
                        OnStep4(model);
                    }
                    break;
                default:
                    break;
            }
            await Task.CompletedTask;
        }

        private void SendTimeout(WheelTimerTimeout<object> timeout)
        {
            try
            {
                if (timeout.IsCanceled) return;

                ulong toid = (ulong)timeout.Task.State;
                timeout.Cancel();
                if (connectCache.TryGetValue(toid, out ConnectCacheModel cache))
                {
                    cache.Canceled = true;
                    _ = SendStep2Fail(toid, cache.NewTunnel);
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error(ex);
            }
        }
        private void AddSendTimeout(ulong toid)
        {
            if (connectCache.TryGetValue(toid, out ConnectCacheModel cache))
            {
                cache.SendTimeout = wheelTimer.NewTimeout(new WheelTimerTimeoutTask<object> { Callback = SendTimeout, State = toid }, 5000);
            }
        }
        private void RemoveSendTimeout(ulong toid)
        {
            if (connectCache.TryGetValue(toid, out ConnectCacheModel cache))
            {
                cache.SendTimeout?.Cancel();
            }
        }


        private async Task OnStep1(PunchHoleStepModel model)
        {
            await clientsTunnel.NewBind(ServerType.UDP, Connection.ConnectId, model.RawData.FromId);

            PunchHoleNotifyInfo data = model.Data as PunchHoleNotifyInfo;
            if (clientInfoCaching.GetUdpserver(model.RawData.FromId, out UdpServer udpServer))
            {

                foreach (var ip in data.LocalIps.Where(c => c.Equals(IPAddress.Any) == false))
                {
                    if (NotIPv6Support(ip))
                    {
                        continue;
                    }
                    udpServer.SendUnconnectedMessage(Helper.EmptyArray, new IPEndPoint(ip, data.LocalPort));
                }
                udpServer.SendUnconnectedMessage(Helper.EmptyArray, new IPEndPoint(data.Ip, data.Port));
                udpServer.SendUnconnectedMessage(Helper.EmptyArray, new IPEndPoint(data.Ip, data.Port + 1));

                AddSendTimeout(model.RawData.FromId);
                await punchHoleMessengerSender.Request(new SendPunchHoleArg<PunchHoleStep2Info>
                {
                    Connection = model.Connection,
                    ToId = model.RawData.FromId,
                    NewTunnel = model.RawData.NewTunnel,
                    Data = new PunchHoleStep2Info { Step = (byte)PunchHoleUdpSteps.STEP_2, PunchType = PunchHoleTypes.UDP }
                }).ConfigureAwait(false);
            }
            else
            {
                Logger.Instance.Warning($"udp OnStep1 未找到通道：{model.RawData.FromId}");
                await SendStep2Fail(model.RawData.FromId, model.RawData.NewTunnel).ConfigureAwait(false);
            }
        }

        private async Task OnStep2(PunchHoleStepModel model)
        {
            await Task.Run(async () =>
             {
                 PunchHoleNotifyInfo data = model.Data as PunchHoleNotifyInfo;
                 if (connectCache.TryGetValue(model.RawData.FromId, out ConnectCacheModel cache) == false)
                 {
                     Logger.Instance.Error($"udp 找不到缓存");
                     await SendStep2Fail(model.RawData.FromId, model.RawData.NewTunnel).ConfigureAwait(false);
                     return;
                 }
                 if (clientInfoCaching.GetUdpserver(model.RawData.FromId, out UdpServer udpServer) == false)
                 {
                     Logger.Instance.Error($"udp 找不到通道服务器：{model.RawData.FromId}");
                     await SendStep2Fail(model.RawData.FromId, model.RawData.NewTunnel).ConfigureAwait(false);
                     return;
                 }

                 List<IPEndPoint> ips = new List<IPEndPoint>();
                 if (UseLocalPort)
                 {
                     var locals = data.LocalIps.Where(c => c.Equals(IPAddress.Any) == false && c.AddressFamily == AddressFamily.InterNetwork).Select(c => new IPEndPoint(c, data.LocalPort)).ToList();
                     ips.AddRange(locals);
                 }
                 if (IPv6Support() && data.Ip.IsLan() == false)
                 {
                     var locals = data.LocalIps.Where(c => c.AddressFamily == AddressFamily.InterNetworkV6).Select(c => new IPEndPoint(c, data.Port)).ToList();
                     ips.AddRange(locals);
                 }
                 ips.Add(new IPEndPoint(data.Ip, data.Port));
                 ips.Add(new IPEndPoint(data.Ip, data.Port + 1));


                 IConnection connection = null;
                 for (int i = 0; i < ips.Count; i++)
                 {
                     IPEndPoint ip = i >= ips.Count - 1 ? ips[^1] : ips[i];
                     if (NotIPv6Support(ip.Address))
                     {
                         continue;
                     }
                     Logger.Instance.DebugDebug($"udp {ip} connect");
                     connection = await udpServer.CreateConnection(ip);
                     if (connection != null)
                     {
                         break;
                     }
                     else
                     {
                         Logger.Instance.DebugError($"udp {ip} connect fail");
                     }
                 }
                 if (connection != null)
                 {
                     await CryptoSwap(connection).ConfigureAwait(false);
                     await SendStep3(connection, model.RawData.FromId, model.RawData.NewTunnel).ConfigureAwait(false);
                 }
                 else
                 {
                     await SendStep2Fail(model.RawData.FromId, model.RawData.NewTunnel).ConfigureAwait(false);
                 }
             });
        }
        private async Task CryptoSwap(IConnection connection)
        {
            if (config.Client.Encode)
            {
                ICrypto crypto = await cryptoSwap.Swap(connection, config.Client.EncodePassword);
                if (crypto == null)
                {
                    Logger.Instance.Error("udp打洞交换密钥失败，可能是两端密钥不一致，A如果设置了密钥，则B必须设置相同的密钥，如果B未设置密钥，则A必须留空");
                }
                else
                {
                    connection.EncodeEnable(crypto);
                }
            }
        }

        private async Task OnStep21(PunchHoleStepModel model)
        {
            PunchHoleNotifyInfo data = model.Data as PunchHoleNotifyInfo;
            if (clientInfoCaching.GetUdpserver(model.RawData.FromId, out UdpServer udpServer))
            {
                foreach (var ip in data.LocalIps.Where(c => c.Equals(IPAddress.Any) == false))
                {
                    if (NotIPv6Support(ip))
                    {
                        continue;
                    }
                    udpServer.SendUnconnectedMessage(Helper.EmptyArray, new IPEndPoint(ip, data.LocalPort));
                }
                udpServer.SendUnconnectedMessage(Helper.EmptyArray, new IPEndPoint(data.Ip, data.Port));
                udpServer.SendUnconnectedMessage(Helper.EmptyArray, new IPEndPoint(data.Ip, data.Port + 1));
            }
            await Task.CompletedTask;
        }

        private void OnStep2Fail(PunchHoleStepModel model)
        {
        }
        private async Task SendStep2Fail(ulong toid, byte newTunnel)
        {
            await punchHoleMessengerSender.Request(new SendPunchHoleArg<PunchHoleStep2FailInfo>
            {
                Connection = Connection,
                ToId = toid,
                NewTunnel = newTunnel,
                Data = new PunchHoleStep2FailInfo { Step = (byte)PunchHoleUdpSteps.STEP_2_Fail, PunchType = PunchHoleTypes.UDP }
            }).ConfigureAwait(false);

            if (connectCache.TryRemove(toid, out ConnectCacheModel cache))
            {
                cache.Canceled = true;
                cache.Tcs.SetResult(new ConnectResultModel
                {
                    State = false,
                    Result = new ConnectFailModel
                    {
                        Msg = "udp打洞失败",
                        Type = ConnectFailType.ERROR
                    }
                });
            }
            else
            {
                OnStepHandler?.Invoke(this, new PunchHoleStepModel
                {
                    Connection = Connection,
                    RawData = new PunchHoleRequestInfo
                    {
                        PunchStep = (byte)PunchHoleUdpSteps.STEP_2_Fail,
                        FromId = toid
                    }
                });
            }
        }

        private async Task SendStep3(IConnection connection, ulong toid, byte newTunnel)
        {
            AddSendTimeout(toid);
            await punchHoleMessengerSender.Request(new SendPunchHoleArg<PunchHoleStep3Info>
            {
                Connection = connection,
                NewTunnel = newTunnel,
                Data = new PunchHoleStep3Info
                {
                    Step = (byte)PunchHoleUdpSteps.STEP_3,
                    PunchType = PunchHoleTypes.UDP
                }
            }).ConfigureAwait(false);
        }
        public async Task OnStep3(PunchHoleStepModel model)
        {
            await punchHoleMessengerSender.Request(new SendPunchHoleArg<PunchHoleStep4Info>
            {
                Connection = model.Connection,
                Data = new PunchHoleStep4Info
                {
                    Step = (byte)PunchHoleUdpSteps.STEP_4,
                    PunchType = PunchHoleTypes.UDP
                }
            }).ConfigureAwait(false);
        }

        public void OnStep4(PunchHoleStepModel model)
        {
            if (connectCache.TryRemove(model.RawData.FromId, out ConnectCacheModel cache))
            {
                cache.Tcs.SetResult(new ConnectResultModel { State = true });
            }
        }

        private bool NotIPv6Support(IPAddress ip)
        {
            return ip.AddressFamily == AddressFamily.InterNetworkV6 && (NetworkHelper.IPv6Support == false || signInState.LocalInfo.Ipv6s.Length == 0);
        }
        private bool IPv6Support()
        {
            return NetworkHelper.IPv6Support == true && signInState.LocalInfo.Ipv6s.Length > 0;
        }

    }
}
