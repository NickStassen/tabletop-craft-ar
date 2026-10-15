package dev.tabletopcraft.ar;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.function.Function;

/**
 * [talk] Which command lines a headset may run (config.yml {@code commands.allow} / {@code commands.deny}, PROTOCOL.md
 * 4.27). Pure and unit-tested; the plugin passes a resolver backed by {@code CommandMap#getCommand(label).getName()}.
 * <ul>
 * <li>The root is the first word, without a leading '/' or a {@code namespace:} prefix ({@code /minecraft:stop} is
 *     {@code stop}), resolved through aliases ({@code rl} is {@code reload}).</li>
 * <li>A non-empty allow list wins: only its roots run. Otherwise everything runs except the deny list.</li>
 * <li>{@code execute ... run <command>} and {@code return run <command>} run another command, so every possible
 *     split after a "run" word is checked too (a player named "run" can't hide {@code stop}: both splits are
 *     checked). Nested executes recurse, up to {@link #MAX_DEPTH}.</li>
 * </ul>
 */
public final class CommandPolicy {
    public static final List<String> DEFAULT_DENY = List.of("stop", "restart", "reload", "op", "deop", "ban", "ban-ip",
        "pardon", "pardon-ip", "whitelist", "save-off", "debug", "jfr", "perf", "transfer");
    /** Commands that run the rest of the line as another command after a "run" word. */
    static final Set<String> RUNNERS = Set.of("execute", "return");
    static final int MAX_DEPTH = 8;

    private final Set<String> allow, deny;
    private final Function<String, String> resolve;

    /**
     * @param resolve label (lower case, no slash or namespace) to the command's canonical name, or null when no such
     *                command exists
     */
    public CommandPolicy(List<String> allow, List<String> deny, Function<String, String> resolve) {
        this.allow = normalise(allow);
        this.deny = normalise(deny);
        this.resolve = resolve;
    }

    private static Set<String> normalise(List<String> l) {
        java.util.HashSet<String> s = new java.util.HashSet<>();
        if (l != null) for (String x : l) if (x != null && !x.isBlank()) s.add(label(x.trim()));
        return Set.copyOf(s);
    }

    /** Outcome: allowed, or refused with the root that was refused and why. */
    public record Decision(boolean allowed, String root, String reason) {
        static Decision ok(String root) { return new Decision(true, root, null); }
    }

    /** The command line without a leading '/' and surrounding spaces. */
    public static String strip(String line) {
        String s = line == null ? "" : line.trim();
        while (s.startsWith("/")) s = s.substring(1).trim();
        return s;
    }

    /** "/Minecraft:Time" -> "time": lower case, no slash, no namespace. */
    public static String label(String word) {
        String w = strip(word).toLowerCase(Locale.ROOT);
        int c = w.indexOf(':');
        return c >= 0 ? w.substring(c + 1) : w;
    }

    /** The first word of a line, as a label ("" for an empty line). */
    public static String rootLabel(String line) {
        String s = strip(line);
        int sp = s.indexOf(' ');
        return label(sp < 0 ? s : s.substring(0, sp));
    }

    public Decision check(String line) { return check(strip(line), 0, true); }

    private Decision check(String line, int depth, boolean top) {
        String[] words = line.trim().split("\\s+");
        String label = label(words[0]);
        if (label.isEmpty()) return new Decision(false, "", "empty command");
        String name = resolve.apply(label);
        if (name == null) {
            // Not a command. At the top the server answers "unknown command" (allowed to try unless an allow list
            // is set); after a "run" word this split simply isn't the real one.
            if (!top) return Decision.ok(label);
            return allow.isEmpty() ? Decision.ok(label) : new Decision(false, label, "/" + label + " is not in commands.allow");
        }
        String canon = label(name);
        for (String n : List.of(label, canon)) if (deny.contains(n)) return new Decision(false, n, "/" + n + " is in commands.deny");
        if (!allow.isEmpty() && !allow.contains(label) && !allow.contains(canon)) return new Decision(false, canon, "/" + canon + " is not in commands.allow");
        if (RUNNERS.contains(canon)) {
            if (depth >= MAX_DEPTH) return new Decision(false, canon, "too many nested commands");
            for (int i = 1; i < words.length - 1; i++) {
                if (!words[i].equals("run")) continue;
                Decision d = check(String.join(" ", Arrays.copyOfRange(words, i + 1, words.length)), depth + 1, false);
                if (!d.allowed()) return d;
            }
        }
        return Decision.ok(canon);
    }

    /** Lower-cased, de-duplicated config list, for logging. */
    public List<String> denyList() { List<String> l = new ArrayList<>(deny); l.sort(null); return l; }
    public List<String> allowList() { List<String> l = new ArrayList<>(allow); l.sort(null); return l; }
}
