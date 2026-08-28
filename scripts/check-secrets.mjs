import { execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'

const names = execFileSync('git', ['ls-files', '-z'], { encoding: 'utf8' })
  .split('\0')
  .filter(Boolean)
const patterns = [
  [/-----BEGIN [A-Z ]+PRIVATE KEY-----/, 'private key material'],
  [/\b(?:ghp|github_pat|xox[baprs])-[A-Za-z0-9-]{20,}\b/, 'hosted-service token'],
  [/\bAKIA[0-9A-Z]{16}\b/, 'AWS access key'],
  [/(?:postgres(?:ql)?|mysql):\/\/[^\s:@]+:[^\s@]+@/i, 'database URL with password'],
  [/(?:api[_-]?key|access[_-]?token|client[_-]?secret|private[_-]?key)\s*[:=]\s*["'][A-Za-z0-9+/=_-]{16,}["']/i, 'credential assignment'],
  [/(?:^|["'])sk-[A-Za-z0-9]{20,}/, 'provider secret key'],
]
const findings = []
const binaryExtensions = /\.(?:png|jpe?g|gif|webp|ico|woff2?|ttf|otf|eot|pdf|zip|gz|br)$/i
for (const name of names) {
  if (binaryExtensions.test(name)) continue
  let source
  try { source = readFileSync(name, 'utf8') } catch { continue }
  for (const [pattern, label] of patterns) {
    const match = source.match(pattern)
    if (!match) continue
    // Test fixtures intentionally use a loopback PostgreSQL URI with obvious placeholder
    // credentials. Keep those deterministic examples in source while still scanning every
    // production-facing file and any non-loopback credential-bearing URI.
    if (label === 'database URL with password' && /(?:^|[\\/])[^\\/]*tests?[^\\/]*[\\/]/i.test(name)) {
      const matchOffset = match.index ?? 0
      const uri = source.slice(matchOffset, matchOffset + 256)
      if (/@(?:localhost|127\.0\.0\.1|\[::1\])(?::\d+)?\//i.test(uri)) continue
    }
    findings.push(`${name}:${source.slice(0, match.index ?? 0).split('\n').length} (${label})`)
  }
}
if (findings.length) {
  console.error('Potential secrets found in tracked files:')
  for (const finding of findings) console.error(`- ${finding}`)
  process.exit(1)
}
console.log(`Secret-pattern scan passed (${names.length} tracked files).`)
