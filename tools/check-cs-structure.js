'use strict';
/*
 * Lightweight C# structural check: strips comments and all string literal forms
 * (regular, verbatim, interpolated, raw), then verifies brace/paren/bracket balance.
 * Not a compiler - just enough to catch structural typos before an MSBuild round trip.
 */

const fs = require('node:fs');
const path = require('node:path');

function stripLiterals(source) {
  let out = '';
  let i = 0;
  const n = source.length;
  const peek = (offset) => source[i + offset];

  while (i < n) {
    const c = source[i];

    if (c === '/' && peek(1) === '/') {
      while (i < n && source[i] !== '\n') i++;
      continue;
    }

    if (c === '/' && peek(1) === '*') {
      i += 2;
      while (i < n && !(source[i] === '*' && source[i + 1] === '/')) i++;
      i += 2;
      continue;
    }

    if (c === '@' && peek(1) === '$') {
      i += 3;
      let depth = 0;
      while (i < n) {
        if (source[i] === '"' && source[i + 1] === '"') { i += 2; continue; }
        if (source[i] === '"' && depth === 0) { i++; break; }
        if (source[i] === '{' && source[i + 1] === '{') { i += 2; continue; }
        if (source[i] === '{') depth++;
        if (source[i] === '}') depth = Math.max(0, depth - 1);
        i++;
      }
      out += '""';
      continue;
    }

    if (c === '$' && peek(1) === '@') {
      i += 3;
      while (i < n) {
        if (source[i] === '"' && source[i + 1] === '"') { i += 2; continue; }
        if (source[i] === '"') { i++; break; }
        i++;
      }
      out += '""';
      continue;
    }

    if (c === '"' && peek(1) === '"' && peek(2) === '"') {
      let quotes = 0;
      while (peek(quotes) === '"') quotes++;
      i += quotes;
      const closing = '"'.repeat(quotes);
      const end = source.indexOf(closing, i);
      i = end === -1 ? n : end + quotes;
      out += '""';
      continue;
    }

    if (c === '"' || (c === '$' && peek(1) === '"')) {
      if (c === '$') i++;
      let depth = 0;
      i++;
      while (i < n) {
        const ch = source[i];
        if (ch === '\\') { i += 2; continue; }
        if (ch === '"' && depth === 0) { i++; break; }
        if (source[i] === '{' && source[i + 1] === '{') { i += 2; continue; }
        if (source[i] === '}' && source[i + 1] === '}') { i += 2; continue; }
        if (ch === '{') depth++;
        if (ch === '}') depth = Math.max(0, depth - 1);
        i++;
      }
      out += '""';
      continue;
    }

    if (c === "'") {
      i++;
      while (i < n) {
        if (source[i] === '\\') { i += 2; continue; }
        if (source[i] === "'") { i++; break; }
        i++;
      }
      out += "''";
      continue;
    }

    out += c;
    i++;
  }

  return out;
}

const dir = process.argv[2] ?? '.';
let failed = 0;

for (const file of fs.readdirSync(dir).filter((f) => f.endsWith('.cs')).sort()) {
  const source = fs.readFileSync(path.join(dir, file), 'utf8');
  const clean = stripLiterals(source);
  const count = (ch) => (clean.match(new RegExp('\\' + ch, 'g')) || []).length;

  const problems = [];
  for (const [open, close] of [['{', '}'], ['(', ')'], ['[', ']']]) {
    const o = count(open);
    const c = count(close);
    if (o !== c) problems.push(`${open}${close} ${o}/${c}`);
  }

  const regions = (clean.match(/^\s*#region/gm) || []).length;
  const endRegions = (clean.match(/^\s*#endregion/gm) || []).length;
  if (regions !== endRegions) problems.push(`#region/#endregion ${regions}/${endRegions}`);

  if (problems.length > 0) failed++;
  console.log(`${file.padEnd(22)} ${problems.length === 0 ? 'ok' : 'MISMATCH -> ' + problems.join(', ')}`);
}

console.log(failed === 0 ? '\nAll files structurally balanced.' : `\n${failed} file(s) need review.`);
process.exit(failed === 0 ? 0 : 1);
