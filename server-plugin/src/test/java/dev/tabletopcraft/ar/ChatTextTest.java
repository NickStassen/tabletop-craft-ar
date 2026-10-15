package dev.tabletopcraft.ar;

import net.kyori.adventure.text.Component;
import net.kyori.adventure.text.format.NamedTextColor;
import net.kyori.adventure.text.format.TextColor;
import org.junit.jupiter.api.Test;

import java.util.List;

import static org.junit.jupiter.api.Assertions.*;

/** [talk] Operator names, headset messages, the chat format and command feedback flattening (PROTOCOL.md 4.26-4.27). */
class ChatTextTest {
    @Test void namesAreSanitised() {
        assertEquals("Nick", ChatText.name("Nick", "Tabletop"));
        assertEquals("Nick_2", ChatText.name(" Ni ck_2!", "Tabletop"));
        assertEquals("abcdefghijklmnop", ChatText.name("abcdefghijklmnopqrstuvwxyz", "Tabletop"));
        assertEquals("cred", ChatText.name("§c<red>", "Tabletop"));
        assertEquals("Tabletop", ChatText.name("§<>! ", "Tabletop"));
        assertEquals("Giant", ChatText.name(null, "Giant"));
        assertEquals("Tabletop", ChatText.name("", "!!!"));
    }

    @Test void messagesLoseControlCharactersAndColourCodes() {
        assertEquals("hello world", ChatText.message("  hello \n\t world  ", 256));
        assertEquals("cred", ChatText.message("§cred", 256));
        assertEquals("ab", ChatText.message("a\u0000\u0007b", 256));
        assertNull(ChatText.message("   ", 256));
        assertNull(ChatText.message(null, 256));
        assertNull(ChatText.message("x".repeat(257), 256));
        assertEquals("x".repeat(256), ChatText.message("x".repeat(256), 256));
    }

    @Test void formatKeepsTagsTypedOnTheHeadsetLiteral() {
        Component c = ChatText.format(ChatText.DEFAULT_FORMAT, "Nick", "<red>hi</red> <click:run_command:/op me>x</click>");
        assertEquals("[Giant] Nick: <red>hi</red> <click:run_command:/op me>x</click>", ChatText.plain(c));
        assertEquals(NamedTextColor.GOLD, colourOf(c, "[Giant]"), "the prefix is gold");
        assertNull(colourOf(c, "<red>hi</red> <click:run_command:/op me>x</click>"), "the message stays uncoloured");
        assertEquals("* [Giant] Nick waves", ChatText.plain(ChatText.format(ChatText.DEFAULT_ME_FORMAT, "Nick", "waves")));
        // A broken format in config.yml falls back to the default instead of failing the send.
        assertEquals("[Giant] Nick: ok", ChatText.plain(ChatText.format("", "Nick", "ok")));
    }

    /** Colour of the first component in the tree whose own text is exactly text (null if none or uncoloured). */
    static TextColor colourOf(Component c, String text) {
        if (c instanceof net.kyori.adventure.text.TextComponent t && t.content().equals(text)) return c.color();
        for (Component ch : c.children()) { TextColor x = colourOf(ch, text); if (x != null) return x; }
        return null;
    }

    @Test void errorsAreRed() {
        assertTrue(ChatText.isError(Component.text("Unknown item", NamedTextColor.RED)));
        assertTrue(ChatText.isError(Component.empty().append(Component.text("No entity was found", NamedTextColor.RED))));
        assertTrue(ChatText.isError(Component.text("x").color(TextColor.color(0xFF5555))));
        assertFalse(ChatText.isError(Component.text("Set the time to 1000")));
        assertFalse(ChatText.isError(Component.text("ok", NamedTextColor.GREEN)));
        assertFalse(ChatText.isError(Component.text("a").append(Component.text("b", NamedTextColor.RED))));
        assertFalse(ChatText.isError(null));
    }

    @Test void linesSplitAndCut() {
        assertEquals(List.of("a", "b"), ChatText.lines("a\nb\n", 10));
        assertEquals(List.of(""), ChatText.lines("", 10));
        assertEquals(List.of("abcdefg..."), ChatText.lines("abcdefghijklmnop", 10));
        assertEquals(List.of("x", "", "y"), ChatText.lines("x\r\n\ny", 10));
    }
}
