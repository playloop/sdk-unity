#!/usr/bin/env node
// docs-api-check: keep the onboarding docs honest against the real SDK API,
// without a Unity/C# toolchain. A full compile is not practical here, so this
// is a symbol-level gate targeting the drift class behind the 2026-07-20
// phantom-API sweep:
//
//   1. OPTIONS FIELDS - every field a doc sets in `new PlayloopOptions { ... }`
//      must be a real member of PlayloopOptions. Catches the removed
//      GameId/GameSlug (the exact CS0117 drift the docs were just fixed for).
//   2. METHOD EXISTENCE - every method the docs call on an SDK object must be
//      defined somewhere in Runtime/. Catches invented methods.
//
// Run: node scripts/docs-api-check.mjs

import { readFileSync, existsSync, readdirSync, statSync } from 'node:fs'
import { join, resolve, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const CONFIG = {
  fence: 'csharp',
  docs: ['README.md', 'AGENTS.md', 'SETUP_UNITY.md'],
  srcDir: 'Runtime',
  optionsFile: 'Runtime/PlayloopOptions.cs', // members allowed inside new PlayloopOptions { }
  roots: ['client', '_client', 'playloop', 'PlayloopSdk'],
  // .NET/object builtins callable on any object; not SDK symbols.
  builtins: ['ToString', 'Equals', 'GetHashCode', 'GetType', 'Dispose', 'ConfigureAwait', 'GetAwaiter', 'Wait', 'Invoke', 'Result', 'ContinueWith'],
}

const walk = (dir, out = []) => {
  if (!existsSync(dir)) return out
  for (const e of readdirSync(dir)) {
    const p = join(dir, e)
    if (statSync(p).isDirectory()) walk(p, out)
    else if (p.endsWith('.cs')) out.push(p)
  }
  return out
}

// Public member names (methods + properties) declared anywhere in Runtime.
const memberDecl = [
  /\b(?:public|internal|protected)\s+(?:static\s+|virtual\s+|override\s+|async\s+|sealed\s+|readonly\s+|new\s+)*[\w<>,\[\]\.\?]+\s+([A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*\(/g, // methods
  /\b(?:public|internal|protected)\s+(?:static\s+|readonly\s+|virtual\s+|override\s+)*[\w<>,\[\]\.\?]+\s+([A-Za-z_]\w*)\s*(?:\{\s*get|=>|;)/g, // properties/fields
]
const defined = new Set()
for (const f of walk(resolve(ROOT, CONFIG.srcDir))) {
  const src = readFileSync(f, 'utf8')
  for (const re of memberDecl) for (const m of src.matchAll(re)) defined.add(m[1])
}

// Members allowed inside a `new PlayloopOptions { }` initializer (both the
// options class and its one nested options type live in the same file).
const optionMembers = new Set()
{
  const src = readFileSync(resolve(ROOT, CONFIG.optionsFile), 'utf8')
  for (const m of src.matchAll(/\bpublic\s+[\w<>,\[\]\.\?]+\s+([A-Za-z_]\w*)\s*\{\s*get/g)) optionMembers.add(m[1])
}

const blockRe = new RegExp('```' + CONFIG.fence + '\\n([\\s\\S]*?)```', 'g')
const problems = []
let blocks = 0

// Grab the brace-matched body of `new PlayloopOptions {` ... `}` (depth-1 fields only).
const optionsInitFields = (code) => {
  const fields = []
  const re = /new\s+PlayloopOptions\s*\{/g
  let m
  while ((m = re.exec(code))) {
    let i = m.index + m[0].length, depth = 1, body = ''
    for (; i < code.length && depth > 0; i++) {
      const c = code[i]
      if (c === '{') depth++
      else if (c === '}') { depth--; if (depth === 0) break }
      if (depth === 1) body += c // only the top-level initializer text
    }
    for (const fm of body.matchAll(/([A-Za-z_]\w*)\s*=/g)) fields.push(fm[1])
  }
  return fields
}

for (const rel of CONFIG.docs) {
  const abs = resolve(ROOT, rel)
  if (!existsSync(abs)) continue
  const text = readFileSync(abs, 'utf8')
  for (const bm of text.matchAll(blockRe)) {
    blocks++
    const code = bm[1]
    // 1. PlayloopOptions initializer fields
    for (const field of optionsInitFields(code))
      if (!optionMembers.has(field))
        problems.push(`${rel}: \`new PlayloopOptions { ${field} = ... }\` - PlayloopOptions has no member \`${field}\``)
    // 2. method existence on SDK-rooted chains
    const chainRe = new RegExp('\\b(' + CONFIG.roots.join('|') + ')((?:\\.[A-Za-z_]\\w*)+)\\s*\\(', 'g')
    for (const m of code.matchAll(chainRe)) {
      const segs = m[2].split('.').filter(Boolean)
      const leaf = segs[segs.length - 1]
      if (!defined.has(leaf) && !CONFIG.builtins.includes(leaf))
        problems.push(`${rel}: docs call \`${m[1]}${m[2]}(\` but method \`${leaf}\` is not defined in Runtime/`)
    }
  }
}

console.log(`[docs-api-check] ${blocks} ${CONFIG.fence} block(s) checked (${defined.size} members, ${optionMembers.size} PlayloopOptions fields)`)
if (problems.length) {
  console.error('\n[docs-api-check] FAILED - docs reference an API that does not match Runtime/:\n')
  for (const p of [...new Set(problems)]) console.error(`  ${p}`)
  process.exit(1)
}
console.log('[docs-api-check] clean: PlayloopOptions fields and SDK method calls all resolve.')
