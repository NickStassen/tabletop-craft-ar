// Usage: node second_player.js  (env: MC_HOST, MC_PORT, BOT_MS). Paper must be in offline mode (test only).
// Second Java player (offline auth): walks across the region and logs entity spawns near the giant.
// mineflayer speaks at most 26.1, so it joins the 26.3 test server as a 26.1 client through ViaBackwards (see e2e.sh).
const mineflayer = require('mineflayer');
const bot = mineflayer.createBot({ host: process.env.MC_HOST || '127.0.0.1', port: Number(process.env.MC_PORT || 25565), username: 'TestWalker', auth: 'offline', version: '26.1' });
const seen = {};
const log = (...a) => console.log(new Date().toISOString(), ...a);
// A 26.x server allows one position packet per client tick and only a client tick_end resets that, so without
// it the second position update gets the bot kicked (invalid_player_movement). mineflayer 4.39 never sends it.
// Like the vanilla client, end each tick after its movement packet (physicsTick fires just before that packet).
bot.on('physicsTick', () => process.nextTick(() => bot._client.state === 'play' && bot._client.write('tick_end', {})));
bot.on('spawn', async () => {
  log('SPAWN', bot.entity.position.toString());
  log('waiting for console tp');
  setTimeout(() => { bot.setControlState('forward', true); bot.setControlState('sprint', true); log('WALKING'); }, 3000);
  setTimeout(() => { bot.clearControlStates(); }, 6000);
  setTimeout(async () => {
    try {
      const { Vec3 } = require('vec3');
      const item = bot.inventory.items().find(i => i.name === 'cobblestone');
      if (!item) { log('NO_COBBLE'); return; }
      await bot.equip(item, 'hand');
      const p = bot.entity.position.floored();
      const ref = bot.blockAt(new Vec3(p.x + 1, p.y - 1, p.z));
      await bot.placeBlock(ref, new Vec3(0, 1, 0));
      log('PLACED cobblestone at', ref.position.offset(0, 1, 0).toString());
    } catch (e) { log('PLACE_FAILED', e.message); }
  }, 9000);
  setInterval(() => log('POS', bot.entity.position.toString()), 2000);
});
bot.on('entitySpawn', (e) => {
  const t = e.name || e.type;
  if (['mannequin', 'item_display'].includes(t) || e.type === 'player' && e.username !== bot.username) {
    log('ENTITY_SPAWN', t, 'id=' + e.id, e.position.toString());
    seen[e.id] = t;
  }
});
bot.on('entityMoved', (e) => {
  if (seen[e.id] === 'mannequin' && Math.random() < 0.05) log('ENTITY_MOVED mannequin', e.position.toString(), 'yaw=' + e.yaw.toFixed(2));
});
bot.on('entityUpdate', (e) => {});
bot.on('kicked', (r) => log('KICKED', JSON.stringify(r)));
bot.on('error', (e) => log('ERROR', e.message));
setTimeout(() => {
  const near = Object.values(bot.entities).filter(e => ['mannequin', 'item_display'].includes(e.name));
  near.forEach(e => log('FINAL_ENTITY', e.name, 'id=' + e.id, e.position.toString(), 'attributes=' + JSON.stringify(e.attributes || {})));
  log('SUMMARY seen=' + JSON.stringify(seen));
  bot.quit(); process.exit(0);
}, Number(process.env.BOT_MS || 22000));
