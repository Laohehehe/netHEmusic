// plugins.js — 插件宿主
// 加载 %APPDATA%\netHEmusic\plugins\<id>\ 下的 JS 插件，按 manifest.json 声明的权限开放 API，
// 并提供侧边栏「插件」页面（管理 / 市场 / 插件自己的页面）。
//
// 权限模型（诚实版）：宿主提供的 API 一律按 manifest 的 permissions 白名单放行，
// 没声明的调用直接抛错；数据读写与网络请求还会在 C# 侧再校验一次。
// 但插件代码本身是跑在页面里的，无法阻止它直接碰 document/window —— 所以装插件 = 信任插件。
(function () {
  var HOST = {
    list: [], allPerms: [], dir: '', market: '',
    loaded: {},     // id -> { manifest, ok, error }
    pages: [],      // { pid, id, title, render }
    settings: [],   // { pid, key, label, type, value, onChange, options }
    on: {},         // event -> [{ pid, fn }]
    http: {},       // rid -> { resolve, reject }
    data: {},       // rid -> { resolve, reject }
    settingValues: {},   // "pid/key" -> 值
    marketJson: null,
    marketError: '',
    activePage: '',  // 当前打开的插件页面 id
    rid: 0
  };

  function post(m) { try { NE.post(m); } catch (e) { } }
  function log(msg) { try { post({ type: 'log', msg: '[plugin] ' + msg }); } catch (e) { } }
  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]; }); }
  function el(tag, cls, html) { var e = document.createElement(tag); if (cls) e.className = cls; if (html !== undefined) e.innerHTML = html; return e; }
  function toast(t) { try { window.toast ? window.toast(t) : log(t); } catch (e) { } }

  // 改插件设置项：写进插件自己的数据目录，并回调插件
  HOST.setPluginSetting = function (pid, key, val) {
    HOST.settingValues[pid + '/' + key] = val;
    for (var i = 0; i < HOST.settings.length; i++) {
      var s = HOST.settings[i];
      if (s.pid !== pid || s.key !== key) continue;
      s.value = val;
      try { if (s.onChange) s.onChange(val); } catch (e) { log('设置项 ' + key + ' 回调出错: ' + (e && e.message)); }
    }
    HOST._lastApi = null;
    var target = HOST.loaded[pid];
    if (target && target.manifest && hasPerm(pid, 'filesystem')) {
      post({ type: 'plugin_data_write', id: pid, name: 'settings/' + key, content: String(val), rid: '' });
    }
  };

  function manifestOf(id) { for (var i = 0; i < HOST.list.length; i++) if (HOST.list[i].id === id) return HOST.list[i]; return null; }
  function hasPerm(id, perm) {
    var m = manifestOf(id);
    return !!(m && (m.permissions || []).indexOf(perm) >= 0);
  }
  function needPerm(id, perm, what) {
    if (hasPerm(id, perm)) return true;
    var msg = '插件「' + id + '」没有声明 "' + perm + '" 权限，已拒绝：' + what + '（请在 manifest.json 的 permissions 里声明）';
    log(msg);
    try { console.warn(msg); } catch (e) { }
    return false;
  }

  // ---------------- 事件 ----------------
  HOST.emit = function (evt, payload) {
    var arr = HOST.on[evt];
    if (!arr || !arr.length) return;
    for (var i = 0; i < arr.length; i++) {
      try { arr[i].fn(payload); }
      catch (e) { log('事件 ' + evt + ' 处理出错（' + arr[i].pid + '）：' + (e && e.message)); }
    }
  };

  // ---------------- 插件 API ----------------
  function buildApi(item) {
    var id = item.id;
    var api = {
      id: id,
      name: item.name,
      version: item.version,
      author: item.author,
      dir: item.dir,
      permissions: item.permissions || [],
      has: function (p) { return hasPerm(id, p); },
      log: function () { log(id + ': ' + Array.prototype.slice.call(arguments).join(' ')); },
      toast: function (t) { toast(t); },
      version_host: 'netHEmusic'
    };

    api.css = {
      add: function (text) {
        if (!needPerm(id, 'ui', 'css.add')) return '';
        var s = document.createElement('style');
        s.setAttribute('data-plugin', id);
        s.textContent = String(text || '');
        document.head.appendChild(s);
        return s;
      },
      remove: function (node) { try { if (node && node.parentNode) node.parentNode.removeChild(node); } catch (e) { } }
    };

    api.dom = {
      q: function (sel) { return document.querySelector(sel); },
      qa: function (sel) { return Array.prototype.slice.call(document.querySelectorAll(sel)); },
      add: function (parent, tag, cls, html) { if (!needPerm(id, 'ui', 'dom.add')) return null; var e = el(tag || 'div', cls, html); (parent || document.body).appendChild(e); return e; }
    };

    // 注册插件自己的页面（显示在「插件」页里）
    api.page = {
      register: function (def) {
        if (!needPerm(id, 'ui', 'page.register')) return;
        if (!def || !def.id) return;
        HOST.pages.push({ pid: id, id: def.id, title: def.title || def.id, render: def.render || function () { } });
        log('插件 ' + id + ' 注册页面: ' + def.title);
      }
    };

    // 往设置页加设置项（值由宿主统一存到插件数据目录的 settings/<key>）
    api.settings = {
      add: function (def) {
        if (!needPerm(id, 'settings', 'settings.add')) return;
        if (!def || !def.key) return;
        HOST.settings.push({
          pid: id, key: def.key, label: def.label || def.key,
          type: def.type || 'switch', options: def.options || null,
          value: def.value, onChange: def.onChange
        });
        HOST.settingValues[id + '/' + def.key] = def.value;
      },
      value: function (key) { return HOST.settingValues[id + '/' + key]; },
      set: function (key, val) { HOST.setPluginSetting(id, key, val); }
    };

    api.on = function (evt, fn) {
      if (!needPerm(id, 'events', 'on(' + evt + ')')) return;
      (HOST.on[evt] = HOST.on[evt] || []).push({ pid: id, fn: fn });
    };

    // 插件私有数据（沙箱在 plugin-data/<id>/）
    api.data = {
      read: function (name) {
        if (!needPerm(id, 'filesystem', 'data.read')) return Promise.reject(new Error('no permission'));
        return new Promise(function (res, rej) { var rid = 'd' + (++HOST.rid); HOST.data[rid] = { resolve: res, reject: rej }; post({ type: 'plugin_data_read', id: id, name: name, rid: rid }); });
      },
      write: function (name, content) {
        if (!needPerm(id, 'filesystem', 'data.write')) return Promise.reject(new Error('no permission'));
        return new Promise(function (res, rej) { var rid = 'd' + (++HOST.rid); HOST.data[rid] = { resolve: res, reject: rej }; post({ type: 'plugin_data_write', id: id, name: name, content: String(content == null ? '' : content), rid: rid }); });
      },
      getSync: function () { return null; }   // 同步读不支持，避免阻塞 UI
    };

    // 网络请求（走宿主代理，绕开 CORS）
    api.http = {
      get: function (url) { return api.http.req('GET', url, null); },
      post: function (url, body) { return api.http.req('POST', url, body); },
      req: function (method, url, body) {
        if (!needPerm(id, 'network', 'http')) return Promise.reject(new Error('no permission'));
        return new Promise(function (res, rej) { var rid = 'h' + (++HOST.rid); HOST.http[rid] = { resolve: res, reject: rej }; post({ type: 'plugin_http', id: id, rid: rid, method: method, url: url, body: body == null ? '' : (typeof body === 'string' ? body : JSON.stringify(body)) }); });
      }
    };

    return api;
  }

  function runPlugin(item) {
    var api = buildApi(item);
    try {
      var fn = new Function('nethe', 'console', '"use strict";\n' + item._code + '\n//# sourceURL=nethe-plugin://' + item.id + '/main.js');
      fn(api, window.console);
      HOST.loaded[item.id] = { manifest: item, ok: true, error: '' };
      log('已加载插件 ' + item.id + ' v' + item.version + '（权限: ' + ((item.permissions || []).join(',') || '无') + '）');
    } catch (e) {
      HOST.loaded[item.id] = { manifest: item, ok: false, error: String(e && e.message || e) };
      log('插件 ' + item.id + ' 执行失败: ' + (e && e.message));
    }
  }

  function loadEnabled() {
    for (var i = 0; i < HOST.list.length; i++) {
      var it = HOST.list[i];
      if (!it.enabled || it.broken) continue;
      if (HOST.loaded[it.id]) continue;
      post({ type: 'plugin_code', id: it.id });
    }
  }

  HOST.init = function () { post({ type: 'plugins_list' }); };

  // ---------------- 与 C# 的消息 ----------------
  NE.on('plugins', function (d) {
    HOST.list = (d && d.list) || [];
    HOST.allPerms = (d && d.perms) || [];
    HOST.dir = (d && d.dir) || '';
    HOST.market = (d && d.market) || '';
    loadEnabled();
    if (window.__onPluginsChanged) { try { window.__onPluginsChanged(); } catch (e) { } }
  });

  NE.on('plugin_code', function (d) {
    if (!d || !d.ok) { if (d) log('取插件代码失败 ' + d.id + ': ' + d.error); return; }
    var it = manifestOf(d.id);
    if (!it) return;
    it._code = d.code;
    runPlugin(it);
  });

  NE.on('plugin_data', function (d) {
    var p = d && HOST.data[d.rid];
    if (!p) return;
    delete HOST.data[d.rid];
    if (d.ok) p.resolve(d.content == null ? '' : d.content); else p.reject(new Error(d.error || '读写出错'));
  });

  NE.on('plugin_http', function (d) {
    var p = d && HOST.http[d.rid];
    if (!p) return;
    delete HOST.http[d.rid];
    if (d.ok) p.resolve(d.body || '');
    else p.reject(new Error(d.error || 'HTTP 失败'));
  });

  NE.on('plugin_market', function (d) {
    HOST.marketJson = null; HOST.marketError = '';
    if (!d || !d.ok) { HOST.marketError = (d && d.error) || '市场不可用'; }
    else { try { HOST.marketJson = JSON.parse(d.json); } catch (e) { HOST.marketError = '市场清单解析失败: ' + e.message; } }
    if (window.__onMarketChanged) { try { window.__onMarketChanged(); } catch (e) { } }
  });

  NE.on('plugin_install', function (d) {
    toast(d && d.ok ? ('插件已安装：' + d.id) : ('安装失败：' + ((d && d.error) || '')));
  });


  // ---------------- 「插件」页面 ----------------
  var curTab = 'installed';
  var curPluginPage = '';

  function permChips(perms) {
    var wrap = el('div','plg-perms');
    var list = perms && perms.length ? perms : ['无权限'];
    list.forEach(function (p) {
      var t = el('span','plg-perm' + (p === '无权限' ? ' none' : ''), esc(p));
      t.title = PERM_DESC[p] || p;
      wrap.appendChild(t);
    });
    return wrap;
  }
  var PERM_DESC = {
    ui: '可以注入 CSS、操作界面、注册自己的页面',
    events: '可以监听播放 / 暂停 / 切歌 / 歌词等事件',
    settings: '可以往设置页加自己的设置项',
    filesystem: '可以读写插件自己的数据目录',
    network: '可以发起网络请求（走宿主代理）',
    storage: '可以保存简单的键值数据'
  };

  function installedCard(it) {
    var c = el('div','plg-card' + (it.broken ? ' broken' : ''));
    var head = el('div','plg-head');
    head.appendChild(el('div','plg-name', esc(it.name) + ' <span class="plg-ver">v' + esc(it.version) + '</span>'));
    if (it.author) head.appendChild(el('div','plg-author','by ' + esc(it.author)));
    var sw = el('div','set-switch' + (it.enabled ? ' on' : ''));
    sw.title = it.enabled ? '已启用，点击停用' : '已停用，点击启用';
    sw.onclick = function (e) {
      e.stopPropagation();
      var on = !sw.classList.contains('on');
      sw.classList.toggle('on', on);
      it.enabled = on;
      post({ type: 'plugin_toggle', id: it.id, on: on });
      toast(on ? ('已启用 ' + it.name + '（重载页面后生效）') : ('已停用 ' + it.name));
    };
    head.appendChild(sw);
    c.appendChild(head);

    if (it.description) c.appendChild(el('div','plg-desc', esc(it.description)));
    c.appendChild(permChips(it.permissions));
    if (it.broken) c.appendChild(el('div','plg-err','⚠ manifest.json 有问题，插件已被跳过'));

    var acts = el('div','plg-acts');
    var mine = HOST.pages.filter(function (p) { return p.pid === it.id; });
    mine.forEach(function (p) {
      var b = el('button','plg-btn','打开：' + p.title);
      b.onclick = function () { curPluginPage = it.id + '/' + p.id; paint(); };
      acts.appendChild(b);
    });
    var reloadBtn = el('button','plg-btn','重新加载');
    reloadBtn.onclick = function () { post({ type: 'plugin_reload' }); toast('已重新下发插件代码'); };
    acts.appendChild(reloadBtn);
    var un = el('button','plg-btn danger','卸载');
    un.onclick = function () {
      if (!confirm('卸载插件「' + it.name + '」？\n目录会被移到 plugins\\.trash 下（可手动恢复）。')) return;
      post({ type: 'plugin_uninstall', id: it.id });
    };
    acts.appendChild(un);
    c.appendChild(acts);
    return c;
  }

  function marketCard(p, installedIds) {
    var c = el('div','plg-card');
    var head = el('div','plg-head');
    head.appendChild(el('div','plg-name', esc(p.name || p.id) + ' <span class="plg-ver">v' + esc(p.version || '') + '</span>'));
    if (p.author) head.appendChild(el('div','plg-author','by ' + esc(p.author)));
    c.appendChild(head);
    if (p.description) c.appendChild(el('div','plg-desc', esc(p.description)));
    if (p.permissions && p.permissions.length) c.appendChild(permChips(p.permissions));
    var acts = el('div','plg-acts');
    var has = installedIds.indexOf(p.id) >= 0;
    var b = el('button','plg-btn' + (has ? '' : ' primary'), has ? '重新安装' : '安装');
    b.onclick = function () {
      if (!p.download) { toast('这个插件没有提供下载地址'); return; }
      if (!confirm('安装插件「' + (p.name || p.id) + '」？\n\n它会获得这些权限：' + ((p.permissions || []).join('、') || '无') + '\n\n只安装你信任的插件。')) return;
      post({ type: 'plugin_install', id: p.id, url: p.download });
    };
    acts.appendChild(b);
    c.appendChild(acts);
    return c;
  }

  function paint() { if (window.__paintPlugins) { try { window.__paintPlugins(); } catch (e) { } } }

  HOST.render = function (view) {
    return new Promise(function (resolve) {
      var page = el('div','page');
      page.appendChild(el('h2','page-title','插件'));

      var bar = el('div','plg-bar');
      var tabs = el('div','plg-tabs');
      var tabInstalled = el('button','plg-tab on','已安装');
      var tabMarket = el('button','plg-tab','插件市场');
      tabs.appendChild(tabInstalled); tabs.appendChild(tabMarket);
      bar.appendChild(tabs);

      var tools = el('div','plg-tools');
      var bDir = el('button','plg-btn','打开插件文件夹');
      bDir.onclick = function () { post({ type: 'plugins_open_dir' }); };
      var bReload = el('button','plg-btn','重新加载全部');
      bReload.onclick = function () { post({ type: 'plugin_reload' }); toast('已重新下发插件代码'); };
      tools.appendChild(bDir); tools.appendChild(bReload);
      bar.appendChild(tools);
      page.appendChild(bar);

      var body = el('div','plg-body');
      page.appendChild(body);
      page.appendChild(el('p','muted','插件目录：' + esc(HOST.dir || '（未就绪）') + '　·　插件代码直接跑在界面里，请只安装你信任的插件。'));

      function renderInstalled() {
        body.innerHTML = '';
        if (!HOST.list.length) {
          var empty = el('div','set-group');
          empty.appendChild(el('h3','','还没有插件'));
          empty.appendChild(el('p','muted','把插件文件夹（里面要有 manifest.json）放进上面的目录，然后点「重新加载全部」；也可以切到「插件市场」看看。'));
          body.appendChild(empty);
          return;
        }
        var g = el('div','plg-list');
        HOST.list.forEach(function (it) { g.appendChild(installedCard(it)); });
        body.appendChild(g);
      }

      function renderMarket() {
        body.innerHTML = '';
        if (HOST.marketError) {
          var e1 = el('div','set-group');
          e1.appendChild(el('h3','','市场不可用'));
          e1.appendChild(el('p','muted', esc(HOST.marketError)));
          if (HOST.market) e1.appendChild(el('p','muted','当前市场地址：' + esc(HOST.market)));
          body.appendChild(e1); return;
        }
        if (!HOST.marketJson) {
          var e2 = el('div','set-group');
          e2.appendChild(el('h3','','正在获取市场清单…'));
          body.appendChild(e2);
          post({ type: 'plugin_market' });
          return;
        }
        var list = (HOST.marketJson.plugins || []);
        var installedIds = HOST.list.map(function (x) { return x.id; });
        var gg = el('div','plg-list');
        if (!list.length) {
          gg.appendChild(el('p','muted','市场里暂时还没有插件。'));
          gg.appendChild(el('p','muted','这个市场是 GitHub 话题聚合出来的：把自己的插件做成一个公开仓库、根目录放 manifest.json、再给仓库加上 nethe-plugin 话题，第二天就会自动出现在这里（也可以去 Actions 页手动跑一次 market）。'));
        }
        list.forEach(function (p) { gg.appendChild(marketCard(p, installedIds)); });
        body.appendChild(gg);
      }

      function repaint() {
        tabInstalled.classList.toggle('on', curTab === 'installed');
        tabMarket.classList.toggle('on', curTab === 'market');
        if (curTab === 'market') renderMarket(); else renderInstalled();
      }
      window.__paintPlugins = repaint;

      tabInstalled.onclick = function () { curTab = 'installed'; repaint(); };
      tabMarket.onclick = function () { curTab = 'market'; repaint(); };
      window.__onPluginsChanged = repaint;
      window.__onMarketChanged = repaint;

      view.innerHTML = ''; view.appendChild(page);
      repaint();
      if (!HOST.list.length) post({ type: 'plugins_list' });
      resolve();
    });
  };

  window.PLUGHOST = HOST;
})();
