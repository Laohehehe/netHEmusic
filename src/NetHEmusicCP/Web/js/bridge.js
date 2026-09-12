// bridge.js —— 与 C# 主进程的 WebView2 消息桥
(function () {
    const listeners = {};

    function post(msg) {
        try { window.chrome.webview.postMessage(msg); }
        catch (e) { console.error('bridge post fail', e); }
    }

    function handle(data) {
        if (!data || !data.type) return;
        (listeners[data.type] || []).forEach(fn => { try { fn(data); } catch (e) { console.error(e); } });
    }

    window.chrome.webview.addEventListener('message', (ev) => {
        let data;
        try { data = JSON.parse(ev.data); } catch (e) { data = ev.data; }
        handle(data);
    });

    window.Bridge = {
        post: post,
        on: (type, fn) => { (listeners[type] = listeners[type] || []).push(fn); },
        ready: () => post({ type: 'ready' })
    };
})();
