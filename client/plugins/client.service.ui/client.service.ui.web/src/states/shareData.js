import { inject, provide, reactive } from "vue";

const shareDataKey = Symbol();
export const provideShareData = () => {
    const state = reactive({
        aliveTypes: { 0: '长连接', 1: '短链接' },
        aliveTypesName: { "tunnel": 0, 'web': 1 },
        forwardTypes: { 'forward': 0, 'proxy': 1 },
        clientConnectTypes: { 0: '未连接', 1: '打洞', 2: '节点中继', 4: '服务器中继' },
        serverTypes: { 1: 'TCP', 2: 'UDP', 3: '/' },
        serverImgs: {
            'zg': require('../assets/zg.png'),
            'zgxg': require('../assets/zgxg.png'),
            'xjp': require('../assets/xjp.png'),
            'hg': require('../assets/hg.png'),
            'rb': require('../assets/rb.png'),
            'mg': require('../assets/mg.png'),
        }
    });
    provide(shareDataKey, state);
}
export const injectShareData = () => {
    return inject(shareDataKey);
}