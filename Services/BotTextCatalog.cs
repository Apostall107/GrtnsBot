namespace LfgBot.Services;

/// <summary>
/// Все редактируемые тексты бота с текстами по умолчанию (веб-админка → Bot texts).
/// Лимиты: кнопка 80, подсказка списка 150, вариант списка 100, описание варианта 100, заголовок окна и поля 45.
/// </summary>
public static class BotTextCatalog
{
    private const int Button = 80, Placeholder = 150, Option = 100, ModalTitle = 45, Embed = 4000, Title = 256, Footer = 2048;

    private static readonly List<BotTextDef> Defs = [];
    private static string _group = "";
    private static string? _refresh;

    private static void Group(string name, string? refresh = null) { _group = name; _refresh = refresh; }

    private static void T(string key, string name, string @default, string? placeholders = null, int max = 2000, bool optional = false) =>
        Defs.Add(new BotTextDef(key, _group, name, @default, placeholders, max, Refresh: _refresh, Optional: optional));

    private static void C(string key, string name, uint color) =>
        Defs.Add(new BotTextDef(key, _group, name, $"#{color:x6}", null, 7, IsColor: true, Refresh: _refresh));

    public static IReadOnlyList<BotTextDef> All => Defs;
    public static readonly Dictionary<string, BotTextDef> ByKey;

    static BotTextCatalog()
    {
        // ================= LFG: group post =================
        Group("LFG — group post", "lfgpost");
        T("lfg.post.organizer", "Organizer line (- = hide)", "Organizer: {user}", "{user}", Embed, optional: true);
        T("lfg.post.starts", "Start time line (also in DMs) (- = hide)", "🕒 Starts: {time}", "{time}", Embed, optional: true);
        T("lfg.post.roster", "Roster line (also in DMs) (- = hide)", "Roster: **{filled}/{total}**", "{filled}, {total}", Embed, optional: true);
        T("lfg.post.requirements", "Requirements line (- = hide)", "📋 Requirements: {list} — others go to the reserve", "{list}", Embed, optional: true);
        T("lfg.post.voice", "Voice room line (- = hide)", "🔊 Voice room: {channel}", "{channel}", Embed, optional: true);
        T("lfg.post.open", "Free slot", "*open*", null, 200);
        T("lfg.post.reserve", "Reserve heading (- = hide)", "**Reserve ({count}):**", "{count}", 200, optional: true);
        T("lfg.post.expires", "Auto-delete line (- = hide)", "-# Post will be removed {time}", "{time}", Embed, optional: true);
        T("lfg.post.recruiting", "Status: recruiting", "📝 Recruiting", null, 200);
        T("lfg.post.full", "Status: full", "✅ Group is full", null, 200);
        T("lfg.post.closed", "Status: closed", "🔒 Signups closed", null, 200);
        T("lfg.post.footer", "Footer", "{status} • ID {id}", "{status}, {id}", Footer);
        C("color.lfg.recruiting", "Stripe color: recruiting", 0x3498DB);
        C("color.lfg.full", "Stripe color: full", 0x2ECC71);
        C("color.lfg.closed", "Stripe color: closed", 0x607D8B);
        T("lfg.post.ping", "Ping message above the post", "{role} — LFG for **{activity}**{when}!", "{role}, {activity}, {when} = « — <time>» or empty", 2000);

        // ================= LFG: creating a group =================
        Group("LFG — creating a group");
        T("lfg.wiz.step1", "Step 1 text", "**Step 1/4** — choose the group size.", null, 2000);
        T("lfg.wiz.step1Templates", "Step 1 text (with templates)", "**Step 1/4** — pick a template (activity, slots and requirements in one go) or choose the group size.", null, 2000);
        T("lfg.wiz.sizePlaceholder", "Size menu placeholder", "How many players?", null, Placeholder);
        T("lfg.wiz.sizeOption", "Size option", "{n} players", "{n}", Option);
        T("lfg.wiz.tplPlaceholder", "Template menu placeholder", "📋 Use a template", null, Placeholder);
        T("lfg.wiz.tplOption", "Template option description", "{count} slots: {roles}", "{count}, {roles}", Option);
        T("lfg.wiz.step2", "Step 2 text", "**Step 2/4** — group of {size}. Choose the activity.", "{size}", 1900);
        T("lfg.wiz.actPlaceholder", "Activity menu placeholder", "Choose a dungeon / activity", null, Placeholder);
        T("lfg.wiz.actOption", "Activity option description (- = none)", "Pings the linked role", null, Option, optional: true);
        T("lfg.wiz.custom", "“Custom activity” option", "Custom activity", null, Option);
        T("lfg.wiz.customOption", "“Custom activity” description", "Enter a name manually, no ping", null, Option);
        T("lfg.wiz.noActivities", "Hint: no activities (- = hide)", "-# No activities yet — an admin can add them with `/activity add`.", null, 300, optional: true);
        T("lfg.wiz.customTitle", "Custom activity window: title", "Custom activity", null, ModalTitle);
        T("lfg.wiz.customLabel", "Custom activity window: field", "Name", null, ModalTitle);
        T("lfg.wiz.customHint", "Custom activity window: placeholder", "e.g. World boss farm", null, 100);
        T("lfg.wiz.step3", "Step 3 text", "**Step 3/4** — select slots in the list and click a role. All slots are «All» by default. Slot 1 is yours.", null, 2000);
        T("lfg.wiz.ping", "Step 3: ping line (- = hide)", "Ping: {role}", "{role}", 500, optional: true);
        T("lfg.wiz.noPing", "Step 3: no ping (- = hide)", "Ping: *none (custom activity)*", null, 500, optional: true);
        T("lfg.wiz.you", "Step 3: “(you)” mark", "*(you)*", null, 100);
        T("lfg.wiz.slotsPlaceholder", "Slots menu placeholder", "Select slots, then click a role", null, Placeholder);
        T("lfg.wiz.slotOption", "Slot option", "Slot {n}: {role}", "{n}, {role}", Option);
        C("color.lfg.draft", "Stripe color: draft", 0x206694);
        T("lfg.wiz.step4", "Step 4 text (- = hide)", "**Step 4/4** — start time in the server time zone (`{zone}`).\n-# Everyone will see the time converted to their own time zone.", "{zone}", 1000, optional: true);
        T("lfg.wiz.selected", "Step 4: selected time (- = hide)", "Selected: {time}", "{time}", 500, optional: true);
        T("lfg.wiz.selectedNone", "Step 4: no time selected (- = hide)", "Selected: *no time*", null, 500, optional: true);
        T("lfg.wiz.requirements", "Step 4: requirements line (- = hide)", "📋 Requirements: {list} — others go to the reserve", "{list}", 500, optional: true);
        T("lfg.wiz.datePlaceholder", "Date menu placeholder", "Date", null, Placeholder);
        T("lfg.wiz.today", "“Today”", "Today", null, Option);
        T("lfg.wiz.tomorrow", "“Tomorrow”", "Tomorrow", null, Option);
        T("lfg.wiz.hourPlaceholder", "Hour menu placeholder", "Hour", null, Placeholder);
        T("lfg.wiz.minutePlaceholder", "Minutes menu placeholder", "Minutes", null, Placeholder);
        T("lfg.wiz.typeTime", "“Type manually” button", "Type manually", null, Button);
        T("lfg.wiz.noTime", "“No time” button", "No time", null, Button);
        T("lfg.wiz.back", "“Back” button", "Back", null, Button);
        T("lfg.wiz.next", "“Next” button", "Next", null, Button);
        T("lfg.wiz.publish", "“Publish” button", "Publish", null, Button);
        T("lfg.wiz.cancel", "“Cancel” button", "Cancel", null, Button);
        T("lfg.wiz.dmOn", "“DM role members: ON” button", "DM role members: ON", null, Button);
        T("lfg.wiz.dmOff", "“DM role members: OFF” button", "DM role members: OFF", null, Button);
        T("lfg.wiz.voiceOn", "“Voice room: ON” button", "Voice room: ON", null, Button);
        T("lfg.wiz.voiceOff", "“Voice room: OFF” button", "Voice room: OFF", null, Button);
        T("lfg.wiz.timeTitle", "Time window: title", "Start time", null, ModalTitle);
        T("lfg.wiz.timeLabel", "Time window: field", "Date and time (server time zone)", null, ModalTitle);
        T("lfg.wiz.timeHint", "Time window: placeholder", "21:30 or 15.10 21:30", null, 100);
        T("lfg.wiz.badTime", "Warning: time not recognized", "Could not parse \"{text}\". Use `21:30` or `15.10 21:30` (day.month).", "{text}", 500);
        T("lfg.wiz.pastTime", "Warning: time in the past", "This time is in the past. Pick another one.", null, 500);
        T("lfg.wiz.cancelled", "Creation cancelled", "Signup cancelled.", null, 2000);
        T("lfg.wiz.expired", "Draft expired", "This draft has expired. Start again.", null, 2000);
        T("lfg.wiz.templateGone", "Template deleted", "This template was deleted. Pick another one.", null, 2000);
        T("lfg.wiz.activityGone", "Activity deleted", "This activity was deleted. Pick another one.", null, 2000);
        T("lfg.wiz.published", "Published", "✅ Published: {link}", "{link}", 1000);
        T("lfg.wiz.dmsSending", "Published: DMs being sent", "🔔 Sending DMs to members with the activity role…", null, 500);
        T("lfg.wiz.voiceCreated", "Published: voice room", "🔊 Voice room: {channel}", "{channel}", 500);
        T("lfg.wiz.mirrorHint", "Published: copy to other channels hint", "-# Optional: duplicate the post to other channels — pick them below.", null, 500);
        T("lfg.wiz.mirrorPlaceholder", "Copy-to-channels menu placeholder", "📢 Also post this group in…", null, Placeholder);
        T("lfg.wiz.mirrored", "Copied to channels", "📢 Posted in {count} more channel(s). Copies stay in sync and are deleted with the group.", "{count}", 1000);
        T("lfg.wiz.mirrorNone", "Nothing copied", "Nothing posted — I can't write in those channels or they already have the post.", null, 1000);
        T("lfg.wiz.projectRole", "Project role required", "Groups here are for **{project}** — you need {roles}.", "{project}, {roles}", 1000);

        // ================= LFG: signing up =================
        Group("LFG — signing up");
        T("lfg.sign.which", "Character picker text", "Signing up for **{activity}** as {role}. Which character?", "{activity}, {role}", 1000);
        T("lfg.sign.charPlaceholder", "Character menu placeholder", "Choose a character", null, Placeholder);
        T("lfg.sign.newChar", "“New character” option", "New character", null, Option);
        T("lfg.sign.newCharOption", "“New character” description", "Enter a name manually", null, Option);
        T("lfg.sign.charTitle", "Character window: title", "Character", null, ModalTitle);
        T("lfg.sign.charLabel", "Character window: field", "Character name", null, ModalTitle);
        T("lfg.sign.charHint", "Character window: placeholder", "In-game name", null, 100);
        T("lfg.sign.emptyName", "Empty character name", "Character name cannot be empty.", null, 1000);
        T("lfg.sign.joined", "Signed up", "✅ Signed up for **{activity}**: {role} — **{name}**", "{activity}, {role}, {name}", 1500);
        T("lfg.sign.reserveFull", "Reserve: no free slot", "🕓 No free slot for {role} — you are on the **reserve** list (#{position}) for **{activity}** as **{name}**. The organizer can move you into the group; you'll get a DM if that happens.", "{role}, {position}, {activity}, {name}", 1500);
        T("lfg.sign.reserveRequirements", "Reserve: requirements not met", "🕓 This group requires {requirements} — you are on the **reserve** list (#{position}) for **{activity}** as **{name}**. The organizer can still move you into the group.", "{requirements}, {position}, {activity}, {name}", 1500);
        T("lfg.sign.gsRequirement", "Requirement: GS", "GS {gs}+ (set it with `/character gs`)", "{gs}", 200);
        T("lfg.sign.projectRole", "Project role required", "This group is for **{project}** — you need {roles} to sign up.", "{project}, {roles}", 1000);
        T("lfg.sign.profileHint", "Hint under the signup reply (- = hide)", "-# Optional: pick your class role (its icon is shown next to your name) and set GS / a personal note for this character below.", null, 400, optional: true);
        T("lfg.err.closed", "Error: signups closed", "Signups are closed.", null, 1000);
        T("lfg.err.groupClosed", "Error: group closed", "This group is closed.", null, 1000);
        T("lfg.err.reserveFull", "Error: reserve full", "The reserve list is full.", null, 1000);
        T("lfg.err.noSlots", "Error: no slots for a role", "No free slots for {role}.", "{role}", 1000);
        T("lfg.err.notInGroup", "Error: not in the group (Leave)", "You are not in this group.", null, 1000);
        T("lfg.gone", "Group no longer active", "This group is no longer active.", null, 1000);
        T("lfg.noTime", "“no time”", "*no time*", null, 100);
        T("lfg.or", "Word between roles (“ or ”)", " or ", null, 20);
        T("lfg.and", "Word between requirements (“ and ”)", " and ", null, 20);

        // ================= LFG: character class, note & GS =================
        Group("LFG — class, note & GS");
        T("lfg.profile.classPlaceholder", "Class menu placeholder", "🎭 Class role for {name}", "{name}", Placeholder);
        T("lfg.profile.thisCharacter", "“this character”", "this character", null, 60);
        T("lfg.profile.noClass", "“No class” option", "No class", null, Option);
        T("lfg.profile.noClassOption", "“No class” description", "Don't show a class role", null, Option);
        T("lfg.profile.note", "“Note & GS” button", "Note & GS", null, Button);
        T("lfg.profile.editNote", "“Edit note & GS” button", "Edit note & GS", null, Button);
        T("lfg.profile.modalTitle", "Note window: title", "Character note & GS", null, ModalTitle);
        T("lfg.profile.gsLabel", "Note window: GS field", "GS (gear score, empty = remove)", null, ModalTitle);
        T("lfg.profile.gsHint", "Note window: GS placeholder", "e.g. 1450", null, 100);
        T("lfg.profile.noteLabel", "Note window: note field", "Note (empty = remove)", null, ModalTitle);
        T("lfg.profile.noteHint", "Note window: note placeholder", "e.g. can lead, healer alt", null, 100);
        T("lfg.profile.classSet", "Class set", "🎭 Class set: {role}", "{role}", 500);
        T("lfg.profile.classRemoved", "Class removed", "🎭 Class removed.", null, 500);
        T("lfg.profile.saved", "Note & GS saved", "💬 Saved: note {note}, GS {gs}", "{note}, {gs}", 500);
        T("lfg.profile.set", "“set”", "set", null, 50);
        T("lfg.profile.removed", "“removed”", "removed", null, 50);
        T("lfg.profile.done", "Result for the character", "{result} — for **{name}**. It's saved for this character and shown in your groups.", "{result}, {name}", 1500);
        T("lfg.profile.classUnavailable", "Class not available", "This class is not available in this project.", null, 500);
        T("lfg.profile.badGs", "Wrong GS", "GS must be a number (0–99999).", null, 500);
        T("lfg.profile.notInGroup", "Not in the group anymore", "You are not in this group anymore.", null, 500);
        T("lfg.profile.noName", "Signup without a character name", "Your signup has no character name.", null, 500);

        // ================= LFG: organizer =================
        Group("LFG — organizer");
        T("lfg.manage.head", "Manage menu text (- = hide)", "**Manage {activity}** — free slots: **{free}**, reserve: **{reserve}**.", "{activity}, {free}, {reserve}", 1000, optional: true);
        T("lfg.manage.hint", "Manage menu hint (- = hide)", "-# Move someone to the reserve to free a slot, then move a member from the reserve into it. Members moved into the group get a DM.", null, 500, optional: true);
        T("lfg.manage.toReserve", "“Move to reserve” placeholder", "🔽 Move from the group to the reserve", null, Placeholder);
        T("lfg.manage.promote", "“Move into the group” placeholder", "⬆️ Move from the reserve into the group", null, Placeholder);
        T("lfg.manage.remove", "“Remove from reserve” placeholder", "✖️ Remove from the reserve", null, Placeholder);
        T("lfg.manage.slotOption", "Member option description", "Slot {n} · {role}", "{n}, {role}", Option);
        T("lfg.manage.wants", "Reserve option description", "Wants {role}", "{role}", Option);
        T("lfg.manage.noName", "“no name”", "no name", null, 50);
        T("lfg.manage.attendance", "Attendance placeholder", "✅ Attendance: who came?", null, Placeholder);
        T("lfg.manage.attendanceHint", "Attendance hint (- = hide)", "-# Attendance: select everyone who came — the others are marked as no-show.", null, 300, optional: true);
        T("lfg.manage.came", "Attendance: came", "came", null, Option);
        T("lfg.manage.noShow", "Attendance: didn't come", "didn't come", null, Option);
        T("lfg.manage.notMarked", "Attendance: not marked", "not marked", null, Option);
        T("lfg.manage.changeTime", "“Change time” button", "Change time", null, Button);
        T("lfg.manage.nobody", "Nobody to manage (- = hide)", "Nobody to manage yet.", null, 300, optional: true);
        T("lfg.manage.movedToReserve", "Moved to reserve", "🔽 {user} was moved to the reserve.", "{user}", 500);
        T("lfg.manage.removedFromReserve", "Removed from reserve", "✖️ {user} was removed from the reserve.", "{user}", 500);
        T("lfg.manage.promoted", "Moved into the group", "⬆️ {user} was moved from the reserve into the group and got a DM.", "{user}", 500);
        T("lfg.manage.attendanceSaved", "Attendance saved", "✅ Attendance saved: {count} came.", "{count}", 500);
        T("lfg.manage.onlyOrganizer", "Only the organizer can manage", "Only the organizer can manage this group.", null, 500);
        T("lfg.err.moveSelf", "Error: move yourself", "You can't move yourself — use Leave.", null, 500);
        T("lfg.err.memberGone", "Error: member left the group", "This member is no longer in the group.", null, 500);
        T("lfg.err.reserveFullManage", "Error: reserve full (manage)", "The reserve list is full — remove someone from it first.", null, 500);
        T("lfg.err.reserveGone", "Error: member left the reserve", "This member is no longer in the reserve.", null, 500);
        T("lfg.err.noSlotsManage", "Error: no free slots (manage)", "No free slots — move someone to the reserve first.", null, 500);
        T("lfg.manage.timeTitle", "Change time window: title", "Change the start time", null, ModalTitle);
        T("lfg.manage.timeLabel", "Change time window: field", "New time (server time zone), empty = none", null, ModalTitle);
        T("lfg.manage.badTime", "Time not recognized", "I couldn't read the time. Use `21:30` or `15.10 21:30`.", null, 500);
        T("lfg.manage.pastTime", "Time in the past", "This time is in the past.", null, 500);
        T("lfg.manage.rescheduled", "Time changed", "🕒 New start: {time}. Everyone in the group got a DM.", "{time}", 500);
        T("lfg.close.question", "Close: question", "Close this group? The post will be deleted. Copy the invites below first if you need them.", null, 1000);
        T("lfg.close.confirm", "Close: confirm button", "Close & delete post", null, Button);
        T("lfg.close.keep", "Close: keep button", "Keep", null, Button);
        T("lfg.close.done", "Closed", "✅ Group closed and the post was deleted.", null, 500);
        T("lfg.close.kept", "Kept open", "Group kept open.", null, 500);
        T("lfg.close.onlyOrganizer", "Only the organizer can close", "Only the organizer can close this group.", null, 500);
        T("lfg.invites.groupTitle", "Group invites: title", "📋 Group invites — {activity}", "{activity}", Title);
        T("lfg.invites.groupFooter", "Group invites: footer", "Copy a line and paste it into the game chat", null, Footer);
        T("lfg.invites.dungeonTitle", "Dungeon invites: title", "🏰 Dungeon invites — {activity}", "{activity}", Title);
        T("lfg.invites.dungeonFooter", "Dungeon invites: footer", "Copy a name into the dungeon invite field", null, Footer);
        T("lfg.invites.nobody", "Nobody to invite (- = hide)", "Nobody to invite yet.", null, 300, optional: true);
        T("lfg.invites.noName", "Without a character name (- = hide)", "-# Without a character name: {count}", "{count}", 300, optional: true);
        C("color.lfg.invites", "Stripe color: invites", 0xF1C40F);
        T("lfg.voice.created", "Group voice room created", "🔊 Voice room created: {channel} — everyone can join and listen, only the group can speak.", "{channel}", 500);
        T("lfg.voice.exists", "Group voice room exists", "The voice room already exists: {channel}", "{channel}", 500);
        T("lfg.voice.onlyMembers", "Only members can create the voice room", "Only members of the group can create its voice room.", null, 500);
        T("lfg.voice.failed", "Voice room failed", "I couldn't create the voice room — the bot needs Manage Channels (and Manage Roles) in this category.", null, 500);

        // ================= LFG: DMs and reminders =================
        Group("LFG — DMs & reminders");
        T("lfg.dm.title", "New group DM: title", "📣 LFG: {activity}", "{activity}", Title);
        T("lfg.dm.started", "New group DM: first line (- = hide)", "{user} started a group on **{server}**.", "{user}, {server}", 500, optional: true);
        T("lfg.dm.openSlots", "New group DM: open slots (- = hide)", "Open slots: {list}", "{list}", 1000, optional: true);
        T("lfg.dm.full", "New group DM: full (- = hide)", "The group is full — you can still join the reserve.", null, 500, optional: true);
        T("lfg.dm.footer", "New group DM: footer", "Sign up right here with the role buttons. Don't want these DMs? Press \"DMs off\" on the LFG panel.", null, Footer);
        C("color.lfg.dm", "Stripe color: new group DM", 0x3498DB);
        T("lfg.dm.openGroup", "“Open the group” button", "Open the group", null, Button);
        T("lfg.dm.turnOff", "“Turn off these DMs” button", "Turn off these DMs", null, Button);
        T("lfg.dm.turnOn", "“Turn DMs back on” button", "Turn DMs back on", null, Button);
        T("lfg.dmPref.on", "DMs turned on (panel)", "🔔 DMs on: you will get a DM when a group is started for your roles.", null, 1000);
        T("lfg.dmPref.off", "DMs turned off (panel)", "🔕 DMs off: you will no longer get LFG DMs on this server.", null, 1000);
        T("lfg.dm.onFromDm", "DMs turned on (from a DM)", "🔔 DMs on: you will get a DM when a group is started for your roles on **{server}**.", "{server}", 1000);
        T("lfg.dm.offFromDm", "DMs turned off (from a DM)", "🔕 DMs off: you will no longer get LFG DMs from **{server}**. You can turn them back on here, with 🔔 on the LFG panel or `/lfg-dm on`.", "{server}", 1000);
        T("lfg.dm.notMember", "Not a member of the server", "You are not a member of this group's server.", null, 500);
        T("lfg.dm.promotedTitle", "Moved into the group DM: title", "✅ You're in: {activity}", "{activity}", Title);
        T("lfg.dm.promoted", "Moved into the group DM: text (- = hide)", "{user} moved you from the reserve into the group.\nRole: {role} — **{name}**", "{user}, {role}, {name}", 1000, optional: true);
        C("color.lfg.promoted", "Stripe color: moved into the group", 0x2ECC71);
        T("lfg.dm.timeChangedTitle", "Time changed DM: title", "🕒 Time changed: {activity}", "{activity}", Title);
        T("lfg.dm.timeChanged", "Time changed DM: text", "{user} moved the group.\nNew start: {time}", "{user}, {time}", 1000);
        C("color.lfg.timeChanged", "Stripe color: time changed", 0xE67E22);
        T("lfg.reminder", "Reminder before the start", "⏰ **{activity}** starts {time}! {members}\n-# Organizer: press 📋 Invite to group or 🏰 Invite to dungeon under the post.", "{activity}, {time}, {members}", 2000);

        // ================= Tickets: the ticket message =================
        Group("Tickets — ticket message", "ticketmsg");
        T("ticket.msg.pings", "Pings above the message (- = none)", "{applicant} {leadership}", "{applicant}, {leadership}", 2000, optional: true);
        T("ticket.msg.title", "Title", "🎫 {project} — application", "{project}", Title);
        T("ticket.msg.applicant", "Applicant line (- = hide)", "Applicant: {user}", "{user}", 500, optional: true);
        T("ticket.msg.voice", "Voice line (- = hide)", "Voice: {channel}", "{channel}", 500, optional: true);
        T("ticket.msg.leadership", "Leadership line (- = hide)", "Leadership: {roles}", "{roles}", 1000, optional: true);
        T("ticket.msg.roles", "Roles-on-approve line (- = hide)", "Roles on approve: {roles}", "{roles}", 1000, optional: true);
        T("ticket.msg.howTo", "How it works (- = hide)", "Leadership can **Approve** (the role is given and the ticket closes) or **Deny** (with a reason sent to the applicant). Anyone here can **Close** the ticket.", null, 2000, optional: true);
        T("ticket.msg.approved", "Approved line", "✅ **Approved** by {user}", "{user}", 500);
        T("ticket.msg.footer", "Footer", "Ticket {id}", "{id}", Footer);
        T("ticket.msg.formTitle", "Form answers: title", "📝 Application form", null, Title);
        C("color.ticket.open", "Stripe color: open", 0xE67E22);
        C("color.ticket.approved", "Stripe color: approved", 0x2ECC71);
        C("color.ticket.form", "Stripe color: form answers", 0x3498DB);
        C("color.ticket.custom", "Stripe color: project text", 0x979C9F);

        // ================= Tickets: creating =================
        Group("Tickets — creating");
        T("ticket.pick", "Project choice text", "Choose the project you are applying to:", null, 1000);
        T("ticket.pickPlaceholder", "Project menu placeholder", "Choose a project", null, Placeholder);
        T("ticket.pickOption", "Project option description (- = none)", "Apply to {project}", "{project}", Option, optional: true);
        T("ticket.noProjects", "No projects", "No projects yet. A moderator can add them with `/ticket-project add`.", null, 1000);
        T("ticket.formTitle", "Form window: title", "Application — {project}", "{project}", ModalTitle);
        T("ticket.form.required", "Form: required answer missing", "Answer the question: {question}", "{question}", 500);
        T("ticket.form.tooLong", "Form: answer too long", "«{question}» — up to {max} characters.", "{question}, {max}", 500);
        T("ticket.created", "Ticket created", "✅ Ticket created: {channel}", "{channel}", 500);
        T("ticket.alreadyOpen", "Already has an open ticket", "You already have an open {project} ticket: {channel}", "{project}, {channel}", 500);
        T("ticket.creating", "Already being created", "Your ticket is already being created…", null, 500);
        T("ticket.createFailed", "Channels not created", "I couldn't create the ticket channels. The bot needs Manage Channels and Manage Roles (Discord error: {error}).", "{error}", 500);
        T("ticket.projectGone", "Project no longer exists", "This project no longer exists.", null, 500);
        T("ticket.topic", "Text channel topic", "{project} application — {user}", "{project}, {user}", 1024);
        T("ticket.voiceName", "Voice channel name", "🎙 {project} • {user}", "{project}, {user}", 100);

        // ================= Tickets: approve, deny, close =================
        Group("Tickets — approve, deny, close");
        T("ticket.approvedMsg", "Approved (in the ticket)", "🎉 {applicant} approved by {by} — {roles} granted.", "{applicant}, {by}, {roles}", 1500);
        T("ticket.deniedMsg", "Denied (in the ticket)", "❌ {applicant}'s application was denied by {by}.", "{applicant}, {by}", 1500);
        T("ticket.reason", "Reason line", "**Reason:** {reason}", "{reason}", 700);
        T("ticket.viaWebsite", "Mark after the name for website actions", " (website)", null, 50, optional: true);
        T("ticket.closingIn", "Closing soon", "🔒 This ticket will close in {seconds} seconds.", "{seconds}", 500);
        T("ticket.dm.approved", "DM: approved", "✅ Your application to **{project}** on **{server}** was approved. You got: **{roles}**.", "{project}, {server}, {roles}", 2000);
        T("ticket.dm.denied", "DM: denied", "❌ Your application to **{project}** on **{server}** was declined.", "{project}, {server}", 1500);
        T("ticket.deny.title", "Deny window: title", "Deny the application", null, ModalTitle);
        T("ticket.deny.label", "Deny window: field", "Reason (sent to the applicant)", null, ModalTitle);
        T("ticket.deny.hint", "Deny window: placeholder", "Optional", null, 100);
        T("ticket.onlyStaffApprove", "Only leadership can approve", "Only {project} leadership can approve applications.", "{project}", 500);
        T("ticket.onlyStaffDeny", "Only leadership can deny", "Only {project} leadership can deny applications.", "{project}", 500);
        T("ticket.alreadyApproved", "Already approved", "This application is already approved.", null, 500);
        T("ticket.noRoles", "No roles to give", "This project has no roles to give. Add one with `/ticket-project member-role-add`.", null, 500);
        T("ticket.applicantLeft", "Applicant left", "The applicant has left the server.", null, 500);
        T("ticket.rolesFailed", "Roles not given", "I couldn't give {roles}. Make sure I have **Manage Roles** and my role is **above** them in Server Settings → Roles.", "{roles}", 1000);
        T("ticket.rolesPartlyFailed", "Some roles not given", "⚠️ Couldn't give {roles} — move my role above them in Server Settings → Roles. The ticket stays open; close it when the roles are sorted out.", "{roles}", 1000);
        T("ticket.projectDeleted", "Project deleted", "This project was deleted.", null, 500);
        T("ticket.gone", "Ticket no longer active", "This ticket is no longer active.", null, 500);
        T("ticket.close.question", "Close: question", "Close this ticket? Both the text and voice channels will be deleted.", null, 1000);
        T("ticket.close.confirm", "Close: confirm button", "Close ticket", null, Button);
        T("ticket.close.already", "Already closed", "This ticket is already closed.", null, 500);
        T("ticket.close.closing", "Closing…", "Closing…", null, 200);
        T("ticket.autoCloseWarning", "Inactivity warning", "⏳ This ticket has been quiet for a while. It will be **closed automatically in 24 hours** unless someone writes here. {applicant}", "{applicant}", 1500);

        // ================= Temp voice rooms =================
        Group("Temp voice — rooms");
        T("room.welcome", "Message above the room panel (- = none)", "{user}, this is your room.", "{user}", 1000, optional: true);
        T("room.joinFirst", "Not in a temp room", "Join your temporary room first.", null, 500);
        T("room.onlyOwner", "Only the owner", "Only the room owner {owner} can do this. If they left, use **Claim**.", "{owner}", 500);
        T("room.renameTitle", "Rename window: title", "Rename room", null, ModalTitle);
        T("room.renameLabel", "Rename window: field", "New name", null, ModalTitle);
        T("room.renameHint", "Rename window: placeholder", "e.g. Raid night", null, 100);
        T("room.badName", "Wrong name", "Name must be 1–100 characters.", null, 500);
        T("room.renamed", "Renamed", "Renamed to **{name}**. (Discord allows 2 renames per 10 minutes.)", "{name}", 500);
        T("room.limitTitle", "Limit window: title", "User limit", null, ModalTitle);
        T("room.limitLabel", "Limit window: field", "Max users (0 = no limit)", null, ModalTitle);
        T("room.badLimit", "Wrong limit", "Limit must be from 0 to 99 (0 = no limit).", null, 500);
        T("room.limitRemoved", "Limit removed", "User limit removed.", null, 500);
        T("room.limitSet", "Limit set", "User limit set to **{limit}**.", "{limit}", 500);
        T("room.locked", "Locked", "🔒 Room locked. Permit someone to let them in.", null, 500);
        T("room.unlocked", "Unlocked", "🔓 Room unlocked.", null, 500);
        T("room.permitted", "Permitted", "{user} can now join your room.", "{user}", 500);
        T("room.kicked", "Kicked", "{user} was kicked from your room.", "{user}", 500);
        T("room.kickSelf", "Kick yourself", "You can't kick yourself.", null, 500);
        T("room.kickBot", "Kick the bot", "You can't kick the bot.", null, 500);
        T("room.notMember", "Not on the server", "That member is not on this server.", null, 500);
        T("room.alreadyOwner", "Already the owner", "You already own this room.", null, 500);
        T("room.ownerHere", "Owner is still here", "The owner {owner} is still here.", "{owner}", 500);
        T("room.claimed", "Claimed", "👑 You are now the owner of this room.", null, 500);
        T("room.defaultsReset", "/room defaults reset", "Your room defaults were reset.", null, 500);
        T("room.defaultsSaved", "/room defaults saved", "Saved: new rooms will be **{name}**, limit {limit}.", "{name}, {limit}", 500);
        T("room.defaultsSavedLocked", "/room defaults saved (locked)", "Saved: new rooms will be **{name}**, limit {limit}, locked.", "{name}, {limit}", 500);
        T("room.noLimit", "“none” (no limit)", "none", null, 50);

        // ================= /character =================
        Group("Characters — /character");
        T("char.listTitle", "/character list: heading", "**Your characters:**", null, 200);
        T("char.listProject", "/character list: project heading", "**{project}:**", "{project}", 200);
        T("char.listHint", "/character list: hint (- = hide)", "-# `/character class`, `/character gs`, `/character note` — add `project:` for another game.", null, 300, optional: true);
        T("char.none", "No saved characters", "No saved characters. They are added when you sign up for a group or via `/character add`.", null, 500);
        T("char.added", "Added", "Added: **{name}**{project}", "{name}, {project} = « (Project)» or empty", 500);
        T("char.removed", "Removed", "Removed: **{name}**", "{name}", 500);
        T("char.renamed", "Renamed", "**{old}** → **{new}**.", "{old}, {new}", 500);
        T("char.classSet", "Class set", "**{name}** — class {role}.", "{name}, {role}", 500);
        T("char.classRemoved", "Class removed", "Class removed from **{name}**.", "{name}", 500);
        T("char.classUnavailable", "Class not available", "This class is not available in this project (see LFG → Classes on the site).", null, 500);
        T("char.noteSaved", "Note saved", "Note saved for **{name}**.", "{name}", 500);
        T("char.noteRemoved", "Note removed", "Note removed from **{name}**.", "{name}", 500);
        T("char.gsSet", "GS set", "**{name}** — GS {gs}.", "{name}, {gs}", 500);
        T("char.gsRemoved", "GS removed", "GS removed from **{name}**.", "{name}", 500);
        T("char.badGs", "Wrong GS", "GS must be 0–99999.", null, 500);
        T("char.updatedGroups", "Active groups updated", " Updated {count} active group(s).", "{count}", 200, optional: true);
        T("char.notFound", "Character not found", "Character not found.", null, 500);
        T("char.notFoundAdd", "Character not found (add first)", "Character not found — add it with `/character add` first.", null, 500);
        T("char.unknownProject", "Unknown project", "Unknown project — pick one from the list.", null, 500);
        T("char.projectRole", "Project role required", "Characters of **{project}** need {roles}.", "{project}, {roles}", 500);
        T("char.enterName", "Rename: empty name", "Enter a name.", null, 300);
        T("char.nameTooLong", "Rename: name too long", "Name: up to {max} characters.", "{max}", 300);
        T("char.duplicate", "Rename: name taken", "You already have a character with this name.", null, 300);

        // ================= Verification (captcha) =================
        Group("Verification (captcha)");
        T("captcha.title", "Title", "🤖 Verification required", null, Title);
        T("captcha.text", "Text", "Before using the bot, type the code from the picture.\n{howto}\nClick **Enter code**. Case and spaces don't matter. Can't read it? Click **New picture**.\n\nAttempts left: **{attempts}**", "{howto}, {attempts}", 3000);
        T("captcha.length", "How to read: length", "The picture has **{length} characters**{rows}.", "{length}, {rows}", 300);
        T("captcha.rows", "How to read: rows", " in {rows} rows", "{rows}", 100, optional: true);
        T("captcha.mirrored", "How to read: mirrored", " Every character is **mirrored** — read them left to right as usual.", null, 300, optional: true);
        T("captcha.timeLimit", "Time limit line", "⏱️ Time limit: {time} — after that the attempt counts as wrong.", "{time}", 300);
        C("color.captcha", "Stripe color", 0xE67E22);
        T("captcha.enter", "“Enter code” button", "Enter code", null, Button);
        T("captcha.new", "“New picture” button", "New picture", null, Button);
        T("captcha.modalTitle", "Window: title", "Verification", null, ModalTitle);
        T("captcha.modalLabel", "Window: field", "Code from the picture", null, ModalTitle);
        T("captcha.modalHint", "Window: placeholder", "Spaces are allowed, e.g. K7MRQ A3XHT", null, 100);
        T("captcha.passed", "Passed", "✅ Verified. Run your command again.", null, 500);
        T("captcha.passedFor", "Passed (for some time)", "✅ Verified for {minutes} min. Run your command again.", "{minutes}", 500);
        T("captcha.wrong", "Wrong code", "❌ Wrong code. Try the new picture.", null, 500);
        T("captcha.timedOut", "Time ran out", "⏰ Time ran out — that counted as a wrong attempt. Try the new picture.", null, 500);
        T("captcha.previousTimedOut", "Previous picture timed out", "⏰ Time ran out on the previous picture — that counted as a wrong attempt.", null, 500);
        T("captcha.failed", "No attempts left", "⛔ No attempts left.", null, 300);
        T("captcha.again", "Can use again", "You can use the bot again {time}.", "{time}", 300);
        T("captcha.blocked", "Blocked", "⛔ You failed the verification. You can use the bot again {time}.", "{time}", 500);
        T("captcha.timeoutBlocked", "Time ran out, blocked", "⛔ Time ran out and there are no attempts left. You can use the bot again {time}.", "{time}", 500);
        T("captcha.expired", "Expired", "This verification has expired. Run your command again.", null, 500);

        // ================= General =================
        Group("General");
        T("bot.error", "Something went wrong", "Something went wrong. Please try again.", null, 500);
        T("bot.accessDenied", "No access to a command", "Access denied: {reason}", "{reason}", 500);
        T("lfg.noAccess", "Bot has no access to the channel", "I can't access this channel. Give the bot **View Channel**, **Send Messages** and **Embed Links** here.", null, 1000);

        ByKey = Defs.ToDictionary(d => d.Key);
    }
}
