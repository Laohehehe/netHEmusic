#!/usr/bin/env node
// 从 GitHub 上所有打了 nethe-plugin 话题的仓库聚合出插件市场清单（plugins/market.json）。
//
// 为什么在 CI 里聚合、而不是客户端现查：
//   GitHub 搜索接口未登录只有 10 次/分钟，客户端每开一次市场页就要发几十个请求，必然被限流。
//   放到 Actions 里每天跑一次，客户端只拉一个 JSON，又快又稳。
//
// 插件作者怎么上架：
//   1. 把插件做成一个公开仓库
//   2. 仓库【根目录】放 manifest.json（和插件本身用的是同一个格式）
//   3. 给仓库加 nethe-plugin 话题
//   第二天清单里就会出现，不用提 PR。
//
// 下载地址优先用仓库最新 Release 里的 .zip 资源；没有就用源码 zip
// （GitHub 的源码 zip 会多套一层 <仓库>-<分支> 目录，客户端安装时能认）。

import { writeFileSync } from 'node:fs';

const TOPIC = 'nethe-plugin';
const OUT = 'plugins/market.json';
const TOKEN = process.env.GITHUB_TOKEN || process.env.GH_TOKEN || '';
const UA = 'netHEmusic-market-bot';

const headers = { Accept: 'application/vnd.github+json', 'User-Agent': UA };
if (TOKEN) headers.Authorization = 'Bearer ' + TOKEN;

async function getJson(url) {
  const r = await fetch(url, { headers });
  if (!r.ok) throw new Error(url + ' -> HTTP ' + r.status);
  return r.json();
}
async function getText(url) {
  try {
    const r = await fetch(url, { headers: { 'User-Agent': UA } });
    return r.ok ? await r.text() : null;
  } catch { return null; }
}
function slug(s) {
  return String(s || '').toLowerCase().replace(/[^a-z0-9._-]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 48);
}

console.log('搜索 topic:' + TOPIC + ' ...');
let search;
try {
  search = await getJson('https://api.github.com/search/repositories?q=topic:' + TOPIC + '&sort=updated&per_page=100');
} catch (e) {
  // 搜索失败时绝对不能写出空清单把好的清单覆盖掉
  console.error('搜索失败，保持原清单不动: ' + e.message);
  process.exit(1);
}

const repos = search.items || [];
console.log('命中 ' + repos.length + ' 个仓库');

const plugins = [];
for (const repo of repos) {
  const branch = repo.default_branch || 'main';
  const full = repo.full_name;

  // manifest.json 必须在仓库根目录：客户端安装时只认「根目录有」或「只套一层目录」
  const txt = await getText('https://raw.githubusercontent.com/' + full + '/' + branch + '/manifest.json');
  if (!txt) { console.log('  跳过 ' + full + '：根目录没有 manifest.json'); continue; }
  let mf;
  try { mf = JSON.parse(txt); } catch (e) { console.log('  跳过 ' + full + '：manifest.json 不是合法 JSON'); continue; }

  const id = slug(mf.id || repo.name);
  if (!id) { console.log('  跳过 ' + full + '：算不出插件 id'); continue; }

  let download = 'https://github.com/' + full + '/archive/refs/heads/' + branch + '.zip';
  try {
    const rel = await getJson('https://api.github.com/repos/' + full + '/releases/latest');
    const zip = (rel.assets || []).find(a => (a.name || '').toLowerCase().endsWith('.zip'));
    if (zip) download = zip.browser_download_url;
  } catch (e) { /* 没有 Release 很正常，用源码 zip */ }

  plugins.push({
    id: id,
    name: mf.name || repo.name,
    version: String(mf.version || '0.0.0'),
    author: mf.author || (repo.owner && repo.owner.login) || '',
    description: mf.description || repo.description || '',
    homepage: mf.homepage || repo.html_url,
    permissions: Array.isArray(mf.permissions) ? mf.permissions : [],
    download: download,
    repo: repo.html_url,
    stars: repo.stargazers_count || 0,
    updated: repo.pushed_at || ''
  });
  console.log('  + ' + id + ' (' + full + ')');
}

plugins.sort((a, b) => (b.stars - a.stars) || a.id.localeCompare(b.id));

const out = {
  manifest_version: 1,
  generator: 'topic:' + TOPIC,
  updated: new Date().toISOString().slice(0, 10),
  count: plugins.length,
  plugins: plugins
};
writeFileSync(OUT, JSON.stringify(out, null, 2) + '\n', 'utf8');
console.log('已写入 ' + OUT + '：' + plugins.length + ' 个插件');
