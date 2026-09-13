// api.js — WebView2 bridge + generic NetEase API passthrough (promise)
(function(){
  var pending = {}; var seq = 0;
  function post(msg){ try { window.chrome.webview.postMessage(msg); } catch(e){ console.error(e); } }
  var hooks = {};
  function on(type, fn){ (hooks[type]=hooks[type]||[]).push(fn); }
  window.chrome.webview.addEventListener('message', function(ev){
    var data; try { data = JSON.parse(ev.data); } catch(e){ data = ev.data; }
    if(!data || !data.type) return;
    if(data.type === 'api_result' && data.id && pending[data.id]){ var p=pending[data.id]; delete pending[data.id]; if(data.error) p.reject(new Error(data.error)); else p.resolve(data.data); }
    if(data.type === 'settings'){ var w=settingsWaiters.shift(); if(w) w(data.data); }
    (hooks[data.type]||[]).forEach(function(fn){ try{ fn(data); }catch(e){ console.error(e); } });
  });
  var settingsWaiters = [];
  function getSettings(){ return new Promise(function(res){ settingsWaiters.push(res); post({ type:'get_settings' }); setTimeout(function(){ if(settingsWaiters.indexOf(res)>=0){ settingsWaiters.splice(settingsWaiters.indexOf(res),1); res({}); } }, 5000); }); }
  function setSetting(k,v){ post({ type:'set_setting', key:k, value:String(v) }); }

  function api(name, args, isPost){
    return new Promise(function(resolve, reject){
      var id = 'r'+(++seq);
      pending[id] = { resolve: resolve, reject: reject };
      post({ type:'api', name:name, args:args||{}, post:!!isPost, id:id });
      setTimeout(function(){ if(pending[id]){ delete pending[id]; reject(new Error('timeout '+name)); } }, 25000);
    });
  }
  window.NE = {
    api: api, on: on, post: post, getSettings: getSettings, setSetting: setSetting,
    search: function(kw,limit,offset){ return api('cloudsearch', { keywords:kw, limit:limit||50, offset:offset||0 }); },
    searchHot: function(){ return api('search/hot'); },
    recommend: function(){ return api('recommend/songs'); },
    privateFM: function(){ return api('personal/fm'); },
    topList: function(){ return api('toplist'); },
    playlistDetail: function(id){ return api('playlist/detail', { id:id }); },
    playlistTracks: function(id,limit,offset){ return api('playlist/track/all', { id:id, limit:limit||1000, offset:offset||0 }); },
    topPlaylist: function(cat,limit){ return api('top/playlist', { cat:cat||'全部', limit:limit||30 }); },
    songUrl: function(id){ return api('song/url', { id:id, br:320000 }); },
  lyric: function(id){ return api('lyric/new', { id:id }); },   // 用 lyric/new 才能拿到逐字歌词 yrc
    songDetail: function(ids){ return api('song/detail', { ids:ids }); },
    loginQrKey: function(){ return api('login/qr/key'); },
    loginQrCreate: function(key){ return api('login/qr/create', { key:key, qrimg:'true' }); },
    loginQrCheck: function(key){ return api('login/qr/check', { key:key }); },
    userPlaylist: function(uid, limit){ return api('user/playlist', { uid: uid, limit: limit || 200 }); },
    songDetail: function(ids){ return api('song/detail', { ids: ids }); },
    loginStatus: function(){ return api('login/status'); },
    logout: function(){ return api('logout', {}, true); }
  };
})();
