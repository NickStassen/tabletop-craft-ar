// Usage: node chat_player.js  (env: MC_HOST, MC_PORT, BOT_NAME, BOT_MS). Paper must be in offline mode (test only).
// [talk] A Java player for tools/control_check.py's chat round trip. Driven over stdin, one command per line:
//   say <text>      chat (or a /command when the text starts with "/")
//   quit            leave the server
// It prints one line per event on stdout: SPAWN, MSG <every chat or system line it sees>, KICKED, ERROR, END
// (and ENTITY lines with BOT_ENTITIES=1).
// mineflayer speaks at most 26.1, so it joins the 26.3 test server through ViaBackwards (see tools/e2e.sh).
const mineflayer = require('mineflayer');
const readline = require('readline');
const name = process.env.BOT_NAME || 'TalkWalker';
const bot = mineflayer.createBot({ host: process.env.MC_HOST || '127.0.0.1', port: Number(process.env.MC_PORT || 25565), username: name, auth: 'offline', version: '26.1' });
const out = (...a) => console.log(a.join(' '));
// A 26.x server allows one position packet per client tick and only a client tick_end resets that (second_player.js).
bot.on('physicsTick', () => process.nextTick(() => bot._client.state === 'play' && bot._client.write('tick_end', {})));
bot.on('spawn', () => out('SPAWN', name));
bot.on('messagestr', (text) => out('MSG', text.replace(/\n/g, ' ')));
// [bedrock] With BOT_ENTITIES=1 also ENTITY <name> <id> <x,y,z> for every entity it is sent (tools/bedrock_check.py --java).
if (process.env.BOT_ENTITIES) bot.on('entitySpawn', (e) => out('ENTITY', e.name, e.id, [e.position.x, e.position.y, e.position.z].map((n) => n.toFixed(2)).join(',')));
bot.on('death', () => { out('DIED'); setTimeout(() => { try { bot.respawn(); } catch (e) { out('ERROR respawn', e.message); } }, 500); });
bot.on('kicked', (r) => out('KICKED', JSON.stringify(r)));
bot.on('error', (e) => out('ERROR', e.message));
bot.on('end', () => { out('END'); process.exit(0); });
readline.createInterface({ input: process.stdin }).on('line', (line) => {
  const sp = line.indexOf(' ');
  const cmd = sp < 0 ? line : line.slice(0, sp), arg = sp < 0 ? '' : line.slice(sp + 1);
  if (cmd === 'say') bot.chat(arg);
  else if (cmd === 'quit') bot.quit();
});
setTimeout(() => { out('TIMEOUT'); bot.quit(); process.exit(0); }, Number(process.env.BOT_MS || 120000));
