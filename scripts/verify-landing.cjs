#!/usr/bin/env node
// Validate maintained landing content; archive hashes apply only to unchanged binary assets.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const root = path.resolve(__dirname, '../src/StellarVault.Landing');
const publicRoot = path.join(root, 'wwwroot');
const failures = [];
function walk(dir) { return fs.readdirSync(dir, {withFileTypes:true}).flatMap(e => e.isDirectory() ? walk(path.join(dir,e.name)) : [path.join(dir,e.name)]); }
const files = walk(publicRoot);
for (const file of files) {
  if (/(?:Dockerfile|README\.md|nginx\.conf|source-manifest\.json|docker-compose\.yml|^\.)$/i.test(path.basename(file))) failures.push('Non-public file: '+file);
  if (!file.endsWith('.html')) continue;
  const text = fs.readFileSync(file,'utf8');
  for (const match of text.matchAll(/(?:href|src)=["']([^"']+)["']/g)) {
    const url = match[1];
    if (/^(?:[a-z]+:|#|\/\/)/i.test(url)) continue;
    const pathname = decodeURIComponent(url.split(/[?#]/)[0]);
    const dest = pathname.startsWith('/') ? path.join(publicRoot,pathname) : path.resolve(path.dirname(file),pathname);
    if (!dest.startsWith(publicRoot+path.sep) && dest !== publicRoot) failures.push('Escaping link: '+url);
    else if (!fs.existsSync(dest)) failures.push(path.relative(publicRoot,file)+': missing '+url);
  }
}
const manifest = JSON.parse(fs.readFileSync(path.join(root,'source-manifest.json'),'utf8'));
let originals = 0;
for (const [name, hash] of Object.entries(manifest.sha256)) {
  if (!/\.(?:png|jpe?g|webp|gif|ico|woff2?)$/i.test(name)) continue;
  const file=path.join(publicRoot,name);
  if (!fs.existsSync(file) || crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex')!==hash) failures.push('Changed original binary: '+name);
  originals++;
}
if (failures.length) { console.error(failures.join('\n')); process.exit(1); }
console.log('Landing verified: '+files.length+' public files; local HTML references and '+originals+' original binary assets checked.');
