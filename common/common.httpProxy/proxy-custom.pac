
var proxy = "PROXY {proxy-address}";

function FindProxyForURL(url, host) 
{
    if (shExpMatch(host, "*.mydomain.com"))
    {
        return proxy;
    }
    if (shExpMatch(host, "192.168.*.*"))
    {
        return proxy;
    }
    return "DIRECT";
}
