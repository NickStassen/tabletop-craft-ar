package dev.tabletopcraft.ar;

import net.kyori.adventure.text.Component;
import net.kyori.adventure.text.format.NamedTextColor;
import net.kyori.adventure.text.format.TextColor;
import net.kyori.adventure.text.minimessage.MiniMessage;
import net.kyori.adventure.text.minimessage.tag.resolver.Placeholder;
import net.kyori.adventure.text.serializer.plain.PlainTextComponentSerializer;

import java.util.ArrayList;
import java.util.List;

/**
 * [talk] Text rules for chat and command output (PROTOCOL.md 4.26-4.27), pure and unit-tested.
 * <ul>
 * <li>The operator's name is server policy: sanitised to {@code [A-Za-z0-9_]{1,16}} and only ever shown inside
 *     config.yml {@code chat.format}, whose fixed prefix ("[Giant]") keeps it from passing for a real account.</li>
 * <li>Messages from the headset lose control characters and the legacy colour sign, and are inserted with
 *     {@link Placeholder#unparsed}, so MiniMessage tags typed on the headset stay literal text.</li>
 * <li>Components going to the headset are flattened to plain text (translatable death and command messages render
 *     in en_us through Paper's flattener).</li>
 * </ul>
 */
public final class ChatText {
    public static final String DEFAULT_FORMAT = "<gold>[Giant]</gold> <name>: <message>";
    public static final String DEFAULT_ME_FORMAT = "<gold>* [Giant]</gold> <name> <message>";
    public static final int NAME_MAX = 16;

    private ChatText() {}

    /** Keeps [A-Za-z0-9_], at most 16 characters; empty or null gives the fallback (itself sanitised, else "Tabletop"). */
    public static String name(String wanted, String fallback) {
        String n = keepNameChars(wanted);
        if (n.isEmpty()) n = keepNameChars(fallback);
        return n.isEmpty() ? "Tabletop" : n;
    }

    private static String keepNameChars(String s) {
        if (s == null) return "";
        StringBuilder b = new StringBuilder();
        for (int i = 0; i < s.length() && b.length() < NAME_MAX; i++) {
            char c = s.charAt(i);
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_') b.append(c);
        }
        return b.toString();
    }

    /**
     * A headset message as it may go into the game: control characters (newlines included) and '§' removed, runs of
     * whitespace collapsed, trimmed. Returns null when nothing is left or it's longer than max characters.
     */
    public static String message(String text, int max) {
        if (text == null) return null;
        StringBuilder b = new StringBuilder(text.length());
        boolean space = false;
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            if (c == '§' || c == '\u007f' || (Character.isISOControl(c) && c != '\t' && c != '\n' && c != '\r')) continue;
            if (Character.isWhitespace(c)) { space = true; continue; }
            if (space && b.length() > 0) b.append(' ');
            space = false;
            b.append(c);
        }
        String s = b.toString();
        if (s.isEmpty() || s.length() > max) return null;
        return s;
    }

    /** The line Java players see: chat.format with &lt;name&gt; and &lt;message&gt; as literal text. */
    public static Component format(String format, String name, String message) {
        String f = format == null || format.isBlank() ? DEFAULT_FORMAT : format;
        try {
            return MiniMessage.miniMessage().deserialize(f, Placeholder.unparsed("name", name), Placeholder.unparsed("message", message));
        } catch (RuntimeException e) {   // a broken format in config.yml: fall back to the default
            return MiniMessage.miniMessage().deserialize(DEFAULT_FORMAT, Placeholder.unparsed("name", name), Placeholder.unparsed("message", message));
        }
    }

    public static String plain(Component c) { return c == null ? "" : PlainTextComponentSerializer.plainText().serialize(c); }

    /** True when the component is styled as an error (vanilla command failures are red). */
    public static boolean isError(Component c) {
        if (c == null) return false;
        TextColor col = c.color();
        if (col != null) return col.value() == NamedTextColor.RED.value() || col.value() == NamedTextColor.DARK_RED.value();
        // An uncoloured wrapper around one red child (Component.empty().append(red)) still reads as an error.
        List<Component> ch = c.children();
        return ch.size() == 1 && isError(ch.get(0)) && plain(c).equals(plain(ch.get(0)));
    }

    /** Plain text split into lines, each at most maxChars (longer ones are cut with "..."). */
    public static List<String> lines(String plain, int maxChars) {
        List<String> out = new ArrayList<>();
        for (String l : plain.split("\r?\n", -1)) out.add(l.length() > maxChars ? l.substring(0, Math.max(0, maxChars - 3)) + "..." : l);
        if (out.size() > 1 && out.get(out.size() - 1).isEmpty()) out.remove(out.size() - 1);
        return out;
    }
}
