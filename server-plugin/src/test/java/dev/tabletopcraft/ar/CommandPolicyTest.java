package dev.tabletopcraft.ar;

import org.junit.jupiter.api.Test;

import java.util.List;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.*;

/** [talk] Which headset commands run (PROTOCOL.md 4.27, config.yml commands.allow / commands.deny). */
class CommandPolicyTest {
    /** A small command map: canonical names, Bukkit-style aliases (rl -> reload) and brigadier redirects (tp). */
    static final Map<String, String> COMMANDS = Map.ofEntries(
        Map.entry("time", "time"), Map.entry("weather", "weather"), Map.entry("say", "say"), Map.entry("me", "me"),
        Map.entry("stop", "stop"), Map.entry("reload", "reload"), Map.entry("rl", "reload"), Map.entry("op", "op"),
        Map.entry("execute", "execute"), Map.entry("return", "return"), Map.entry("tp", "tp"), Map.entry("teleport", "teleport"),
        Map.entry("gamerule", "gamerule"), Map.entry("whitelist", "whitelist"), Map.entry("summon", "summon"),
        Map.entry("tabletop", "tabletop"), Map.entry("ban-ip", "ban-ip"));

    static CommandPolicy policy(List<String> allow, List<String> deny) { return new CommandPolicy(allow, deny, COMMANDS::get); }

    static CommandPolicy defaults() { return policy(List.of(), CommandPolicy.DEFAULT_DENY); }

    @Test void plainCommandsRun() {
        CommandPolicy p = defaults();
        for (String ok : new String[] { "time set day", "/time set day", "  weather clear ", "summon minecraft:pig ~ ~1 ~", "tabletop status",
                                        "minecraft:time set noon", "gamerule advance_time false" })
            assertTrue(p.check(ok).allowed(), ok);
        assertEquals("time", p.check("/Minecraft:TIME set day").root());
    }

    @Test void denyListWithNamespacesAndAliases() {
        CommandPolicy p = defaults();
        for (String bad : new String[] { "stop", "/stop", "STOP", "minecraft:stop", "bukkit:reload", "rl", "rl confirm", "op Alex", "whitelist off",
                                         "ban-ip 10.0.0.1" }) {
            CommandPolicy.Decision d = p.check(bad);
            assertFalse(d.allowed(), bad);
            assertTrue(d.reason().contains("commands.deny"), d.reason());
        }
        assertEquals("reload", p.check("rl").root());
    }

    @Test void executeRunIsCheckedForEverySplit() {
        CommandPolicy p = defaults();
        assertTrue(p.check("execute as @a at @s run time set day").allowed());
        assertFalse(p.check("execute as @a run stop").allowed());
        assertFalse(p.check("execute if entity @e[tag=run] run minecraft:stop").allowed());
        // A player named "run": "run stop" isn't a command, but the second split is.
        assertFalse(p.check("execute as run run stop").allowed());
        // Nested executes, and return run (1.20.2+).
        assertFalse(p.check("execute as @a run execute at @s run op Alex").allowed());
        assertFalse(p.check("return run stop").allowed());
        assertTrue(p.check("execute positioned 1 2 3 run summon minecraft:lightning_bolt ~ ~ ~").allowed());
        // "run" as the last word runs nothing.
        assertTrue(p.check("execute as @a run").allowed());
    }

    @Test void deepNestingIsRefused() {
        String line = "time set day";
        for (int i = 0; i <= CommandPolicy.MAX_DEPTH; i++) line = "execute run " + line;
        assertFalse(defaults().check(line).allowed());
    }

    @Test void allowListWins() {
        CommandPolicy p = policy(List.of("time", "/weather", "execute"), List.of());
        assertTrue(p.check("time set day").allowed());
        assertTrue(p.check("weather rain").allowed());
        assertTrue(p.check("execute positioned 0 0 0 run time set day").allowed());
        assertFalse(p.check("summon minecraft:pig").allowed());
        assertFalse(p.check("execute run summon minecraft:pig").allowed());
        assertFalse(p.check("nosuchcommand").allowed());   // unknown roots aren't in the allow list either
        assertTrue(p.check("summon x").reason().contains("commands.allow"));
    }

    @Test void unknownCommandsAreLeftToTheServer() {
        assertTrue(defaults().check("nosuchcommand 1 2").allowed());   // the server answers "unknown command"
        assertFalse(defaults().check("   ").allowed());
        assertFalse(defaults().check("/").allowed());
    }

    @Test void labelsAndRoots() {
        assertEquals("time", CommandPolicy.label("/minecraft:Time"));
        assertEquals("say", CommandPolicy.rootLabel("/say hello there"));
        assertEquals("", CommandPolicy.rootLabel(""));
        assertEquals("time set day", CommandPolicy.strip(" //time set day "));
    }

    @Test void denyListFromConfigIsNormalised() {
        CommandPolicy p = policy(List.of(), List.of("/Minecraft:Summon", " TP "));
        assertFalse(p.check("summon minecraft:pig").allowed());
        assertFalse(p.check("tp Alex 0 0 0").allowed());
        assertTrue(p.check("teleport Alex 0 0 0").allowed());   // brigadier redirects are separate commands
        assertEquals(List.of("summon", "tp"), p.denyList());
    }

    @Test void limiterBurstThenRate() {
        EditLimiter l = new EditLimiter(2, 5);
        long t = 1_000_000_000L;
        int ok = 0;
        for (int i = 0; i < 10; i++) if (l.tryAcquire(t)) ok++;
        assertEquals(5, ok, "burst of 5");
        assertFalse(l.tryAcquire(t + 400_000_000L));   // 0.8 token
        assertTrue(l.tryAcquire(t + 500_000_000L));    // 1 token after 0.5 s at 2/s
        assertFalse(l.tryAcquire(t + 500_000_000L));
    }
}
