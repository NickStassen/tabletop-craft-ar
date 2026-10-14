package dev.tabletopcraft.ar;

import org.junit.jupiter.api.Test;

import java.io.IOException;
import java.util.UUID;

import static org.junit.jupiter.api.Assertions.*;

class SkinLookupTest {
    @Test void parsesMojangProfileResponses() throws Exception {
        UUID id = SkinLookup.parseProfile(200, "{\"id\":\"0123456789abcdef0123456789abcdef\",\"name\":\"Steve\"}");
        assertEquals(UUID.fromString("01234567-89ab-cdef-0123-456789abcdef"), id);
        assertNull(SkinLookup.parseProfile(204, ""));
        assertNull(SkinLookup.parseProfile(404, "{\"errorMessage\":\"Couldn't find any profile with name x\"}"));
        assertThrows(IOException.class, () -> SkinLookup.parseProfile(429, ""));
    }

    @Test void rejectsNamesThatAreNotJavaAccounts() throws Exception {
        assertTrue(SkinLookup.VALID_NAME.matcher("Steve_123").matches());
        assertFalse(SkinLookup.VALID_NAME.matcher("a/../b").matches());
        assertFalse(SkinLookup.VALID_NAME.matcher("seventeen_chars__").matches());
        assertNull(SkinLookup.uuidForName("not a name"));   // no request made
    }

    @Test void modelNames() {
        assertEquals("slim", SkinLookup.model(true));
        assertEquals("wide", SkinLookup.model(false));
    }
}
