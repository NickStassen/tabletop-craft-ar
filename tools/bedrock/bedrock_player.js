// Usage: node bedrock_player.js  (env: BE_HOST, BE_PORT, BOT_NAME, BE_VERSION, BOT_MS).
// [bedrock] A Bedrock Edition player for tools/bedrock_check.py: joins a Paper server through Geyser + Floodgate with
// PrismarineJS bedrock-protocol (offline login, so the test server's Geyser needs
// advanced.bedrock.validate-bedrock-login: false; test only). Driven over stdin, one command per line:
//   say <text>      chat (a "/command" when the text starts with "/")
//   quit            leave the server
// It prints one line per event on stdout:
//   SPAWN <name> <runtime_id>, MSG <type> <text>, KICKED <reason>, ERROR <message>, END,
//   PLAYER <runtime_id> <username> <uuid> <x,y,z> scale=<s>   (add_player: other players and Mannequins)
//   ENTITY <runtime_id> <entity_type> <x,y,z> scale=<s> name=<name tag> (add_entity)
//   ITEM <runtime_id> <x,y,z>                                   (add_item_entity: dropped items)
//   EQUIP <runtime_id> <item network id> (mob_equipment), ARMOR <runtime_id> <helmet network id>,
//   DATA <runtime_id> scale=<s> | name=<name tag> (set_entity_data carrying either), GONE <runtime_id> (remove_entity),
//   MOVED <runtime_id> (first move of each entity, so the check sees which ones follow the poses).
const bedrock = require('bedrock-protocol');
const readline = require('readline');

const name = process.env.BOT_NAME || 'BedrockBot';
const out = (...a) => console.log(a.join(' '));
const v3 = (p) => p ? [p.x, p.y, p.z].map((n) => Number(n).toFixed(2)).join(',') : '?';

/** Reads a key out of a bedrock-protocol MetadataDictionary (an array of {key, type, value}). */
function meta(md, key) {
  for (const e of md || []) if (e.key === key) return e.value;
  return undefined;
}

const client = bedrock.createClient({
  host: process.env.BE_HOST || '127.0.0.1',
  port: Number(process.env.BE_PORT || 19132),
  username: name,
  offline: true,
  version: process.env.BE_VERSION || undefined,
  skipPing: !!process.env.BE_VERSION,
});

let selfRuntime = null;
const moved = new Set();
client.on('start_game', (p) => { selfRuntime = String(p.runtime_entity_id); });
client.on('spawn', () => out('SPAWN', name, selfRuntime));
client.on('text', (p) => {
  const params = p.parameters && p.parameters.length ? ' [' + p.parameters.join('|') + ']' : '';
  out('MSG', p.type, ((p.source_name ? '<' + p.source_name + '> ' : '') + (p.message || '')).replace(/\n/g, ' ') + params);
});
client.on('add_player', (p) => out('PLAYER', String(p.runtime_id), JSON.stringify(p.username), p.uuid, v3(p.position),
  'scale=' + meta(p.metadata, 'scale'), 'item=' + (p.held_item && p.held_item.network_id)));
client.on('add_entity', (p) => out('ENTITY', String(p.runtime_id), p.entity_type, v3(p.position),
  'scale=' + meta(p.metadata, 'scale'), 'name=' + JSON.stringify(meta(p.metadata, 'nametag') ?? null)));
client.on('add_item_entity', (p) => out('ITEM', String(p.runtime_id), v3(p.position)));
client.on('mob_equipment', (p) => out('EQUIP', String(p.runtime_entity_id), p.item && p.item.network_id));
client.on('mob_armor_equipment', (p) => out('ARMOR', String(p.runtime_entity_id), p.helmet && p.helmet.network_id));
client.on('set_entity_data', (p) => {
  const s = meta(p.metadata, 'scale'), n = meta(p.metadata, 'nametag');
  if (s !== undefined) out('DATA', String(p.runtime_entity_id), 'scale=' + s);
  if (n !== undefined) out('DATA', String(p.runtime_entity_id), 'name=' + JSON.stringify(n));
});
client.on('remove_entity', (p) => out('GONE', String(p.entity_id_self)));
for (const t of ['move_entity', 'move_entity_delta', 'move_player']) {
  client.on(t, (p) => {
    const id = String(p.runtime_entity_id ?? p.runtime_id);
    if (id === selfRuntime || moved.has(id)) return;
    moved.add(id);
    out('MOVED', id);
  });
}
// BE_TRACE=1: also PKT <name> for every packet type after the spawn, once each (to see what Geyser sends).
if (process.env.BE_TRACE) {
  const seen = new Set();
  client.on('packet', (d) => {
    const n = d && d.data && d.data.name;
    if (n && selfRuntime && !seen.has(n)) { seen.add(n); out('PKT', n); }
  });
}
client.on('kick', (r) => out('KICKED', JSON.stringify(r.message || r)));
client.on('error', (e) => out('ERROR', e && e.message));
client.on('close', () => { out('END'); process.exit(0); });

function chat(text) {
  if (text.startsWith('/')) {
    client.queue('command_request', {
      command: text, origin: { type: 'player', uuid: '00000000-0000-0000-0000-000000000000', request_id: '', player_entity_id: 0n },
      internal: false, version: 'latest',
    });
    return;
  }
  client.queue('text', {
    needs_translation: false, category: 'authored', type: 'chat', source_name: name, message: text,
    xuid: '', platform_chat_id: '', has_filtered_message: false,
  });
}

readline.createInterface({ input: process.stdin }).on('line', (line) => {
  const sp = line.indexOf(' ');
  const cmd = sp < 0 ? line : line.slice(0, sp), arg = sp < 0 ? '' : line.slice(sp + 1);
  try {
    if (cmd === 'say') chat(arg);
    else if (cmd === 'quit') { client.disconnect(); setTimeout(() => process.exit(0), 500); }
  } catch (e) { out('ERROR', e.message); }
});
setTimeout(() => { out('TIMEOUT'); client.disconnect(); process.exit(0); }, Number(process.env.BOT_MS || 120000));
