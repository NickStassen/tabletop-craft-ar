# Store listing draft (Meta Horizon Store)

Draft text for the Developer Dashboard's App Metadata, which the release-channel invite page also shows (name, short
description, header image). Review it, then paste it in. Field limits: name 40 characters, short description 500,
long description 1500 (each draft below fits; `tools/release/` doesn't check them, so count again after edits).

Rules this text follows (Minecraft Usage Guidelines, Meta app policies):
- "Minecraft" is never the first or main word of the title and never in the icon or art; no Minecraft logo, no font in
  the style of the logo, no textures, screenshots or sounds from the game in the store media (they count as Mojang's
  assets). Store media come from the app with original or permissively licensed textures (demo mode or a CC0 pack).
- The exact disclaimer is shown prominently: in the long description, in the app's first-run card and Info tab, on the
  privacy page and in the README.
- Requirements are stated plainly: the user needs their own Minecraft: Java Edition and a PC on the same Wi-Fi.
- No Meta trademarks except references to the hardware ("Meta Quest 3").
- A contact that isn't a chat or forum link (the Minecraft guidelines ask for one): an email address.

## Fields

**App name** (17 characters):

> Tabletop Craft AR

**Short description** (about 340 characters):

> Your Java Edition server as a living miniature on your real table. See players, mobs and redstone move, build and
> break blocks with your hands, chat, and loom over the world as a giant your friends can see. Needs your own Minecraft:
> Java Edition and a PC running the free server starter on the same Wi-Fi. NOT AN OFFICIAL MINECRAFT PRODUCT.

**Long description** (about 1,300 characters):

> Tabletop Craft AR puts a live Minecraft: Java Edition world on your table in mixed reality. The server runs on your
> own PC; the headset shows it as a miniature in passthrough, updated as players build, mobs wander and the sun sets.
>
> - Glide over the world, zoom and turn the table, and pick how wide an area you see.
> - Break, place and pick blocks; use doors, levers, buckets and tools; undo, fill boxes and paint with brushes.
> - Chat with players and run server commands from a panel on your wrist; mute, block or report players.
> - Your friends see you as a giant head and hands above their world, moving as you do.
> - Spatial sound, day and night, weather, signs, banners and chests, all from the game files on your own PC.
> - Bedrock players can join too (Geyser and Floodgate).
>
> Setup takes a few minutes: start Minecraft: Java Edition once on your PC, run the free server starter, open the app
> at a table and type the 6-digit code it shows into the server window. The headset then downloads the textures and
> sounds from your PC over your Wi-Fi. Nothing from the game is included in this app.
>
> Requires: Meta Quest 3, a PC (Windows, macOS or Linux) on the same Wi-Fi running the server starter, and your own
> copy of Minecraft: Java Edition.
>
> NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.

**Other fields**

| Field | Value |
|---|---|
| Category / genre | Creativity, Simulation (mixed reality) |
| Age group (self-certification) | TEENS_AND_ADULTS (13+) |
| Release channel | BETA (invite link, auto-approve), then a public listing with the Early Access badge |
| Supported devices | Meta Quest 3, Quest 3S |
| Play area / mode | Stationary, seated or standing; mixed reality (passthrough) |
| Input | Touch controllers |
| Comfort | Comfortable: the world never moves you; you move the table |
| Internet | Local network (Wi-Fi) required; internet only for player skins |
| Website | https://github.com/NickStassen/tabletop-craft-ar |
| Privacy policy URL | https://nickstassen.github.io/tabletop-craft-ar/privacy.html (GitHub Pages from `docs/`) |
| Support / contact email | nicholas@stassen.com (public: privacy page and store) |
| Publisher name | Nick Stassen (developer shown as "Nick Stassen Hobbies") |
| Data Use Checkup | not needed: the app uses no Platform SDK features (Meta's User Reporting Service is set up in the dashboard, with no SDK in the app) |
| Player modes | Multiplayer (online with invited players: the server's whitelist and pairing), once User Reporting is active (below) |

**Store media checklist** (Meta's asset guidelines): icon 512x512 (opaque, square corners), hero 3000x900, landscape
2560x1440, square 1440x1440, portrait 1008x1440, five screenshots at 2560x1440 from the real app with no text on them,
a trailer of 30 s to 2 min. All original art: no Minecraft logo, font, textures or screenshots of vanilla textures.
The invite page needs the name, the short description and the header image (hero) only.

## User reporting, blocking and the content rating (v1.2.0)

What the public listing needs, and what to set in the Developer Dashboard once v1.2.0 is uploaded. The invite-only beta
doesn't need it.

**Meta's requirement.** [VRC.Content.3](https://developers.meta.com/horizon/resources/vrc-content-3/): apps with
user interaction, including synchronous multiplayer with text chat, "must have a form for users to notify the
developer" about conduct against the Code of Conduct for Virtual Experiences, and it "must always be accessible to the
user from the" Meta Quest button; an in-app Report is allowed as long as the button route works too. Muting and
blocking aren't required by a VRC (the dashboard's IARC questionnaire asks whether users can block and report). Meta
offers two tools: the [User Reporting Service](https://developers.meta.com/horizon/resources/reporting-service/), a
hosted form configured only in the dashboard that delivers reports by email or an encrypted inbox, and the
[User Reporting Plugin](https://developers.meta.com/horizon/resources/reporting-plugin), which hands the Quest
button's Report to an in-app flow and needs the Platform SDK (entitlement check, Data Use Checkup). See also
[the 2023 announcement](https://developers.meta.com/horizon/blog/user-reporting-requirements-developer-tools-updates/).

**Decision: the hosted User Reporting Service, no Platform SDK.** It satisfies VRC.Content.3 without any code, so the
app keeps using no Platform SDK features. The in-app tools are for what the developer can't do: the people a headset
user chats with are players on a private server, and only that server's operators can kick, ban or take them off the
whitelist, so the in-app **Report** goes to them (`report_v1`, `reports.log`, PROTOCOL.md "Chat safety"), and
**Mute** / **Block** hide a player's chat on the headset. Reports through the Quest button reach the developer, who
answers them and points to the server's owner.

**Developer Dashboard steps.**
1. My Apps > Tabletop Craft AR > Platform Services > **User Reporting** > Add Service > Get Started.
2. Primary reporting tool: **User Reporting Service** (not the plugin: that needs the Platform SDK).
3. Delivery: **email** to the support address above (or the secure inbox: generate and keep the decryption key; a
   lost key can't be recovered). Ask for the reporter's email: yes (to answer them).
4. Reasons (5 to 7, short): Harassment or bullying; Hate speech; Sexual content; Threats or violence; Spam or scams;
   Cheating or griefing; Something else. Optional: a 1440x2560 branding image (original art, no Minecraft assets).
5. Activate. Then App Submissions > App Metadata > Specs: set the player mode to **Multiplayer**; the dashboard
   should now accept it with User Reporting active. Check the Report Abuse form from the Quest button on a headset
   with the release-channel build (VRC.Content.3 test steps: start the app, connect to a server, press the Meta
   button, Report, submit, see the success dialog).

**IARC questionnaire: answers to change (v1.2.0).**

| Question | Was | Now |
|---|---|---|
| Users can interact or exchange text (chat) | yes | yes (unchanged) |
| Users can block other users | no | **yes**: Mute and Block in the operator panel, per server |
| Users can report other users | no | **yes**: Report in the operator panel (to the server's operators) and the Quest button's Report Abuse (to the developer) |
| Chat is moderated | no | no (no filtering; the server's operators get reports and can act, but there is no moderation service) |
| Interaction limited to invited friends | yes | yes (unchanged: the server's whitelist and pairing) |

The privacy policy (`docs/privacy.html`) already describes both report routes and what each stores; it says the Quest
menu has Report Abuse, so activate the service before the public listing goes live.
