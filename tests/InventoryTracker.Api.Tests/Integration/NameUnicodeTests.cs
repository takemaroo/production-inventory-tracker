using System.Net;
using System.Net.Http.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Sdk;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// BUG-002, decisions #170 and #171: the one name rule, through the API, for all three name lists: units (AC-06.2),
/// items (AC-07.1) and customers (AC-29.2). A name is compared and stored in Unicode NFC, so the same letters in
/// another Unicode form are a duplicate; a name with nothing visible is "Name is required."; the length counts what is
/// stored. Each test runs once per list, for create and for rename. T-034 (SEC-25, decisions #176 and #181) adds the
/// characters that are not allowed and the unchanged-stored-name case; its tests are the section marked T-034.
/// </summary>
/// <remarks>
/// Tests share one database (<see cref="IClassFixture{TFixture}"/>), so every name carries a unique suffix. Special
/// characters are written as escapes so the two Unicode forms cannot be swapped by an editor.
/// </remarks>
public class NameUnicodeTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>The three lists that share the rule; each test is a theory over them.</summary>
    public static TheoryData<string> Lists => ["units", "items", "customers"];

    /// <summary>Every list, with each invisible-only name decision #171 must refuse (written as JSON escapes).</summary>
    public static TheoryData<string, string> ListsAndInvisibleNames()
    {
        var data = new TheoryData<string, string>();
        foreach (var list in new[] { "units", "items", "customers" })
        {
            data.Add(list, @"\u200B");             // zero width space alone (BUG-002 notes)
            data.Add(list, @"  \u200B \t ");       // white space around it
            data.Add(list, @"\uFEFF\u2060\u200D"); // byte order mark, word joiner, zero width joiner
            data.Add(list, @"\t\u0000");           // tab and NUL (control characters)
        }

        return data;
    }

    /// <summary>
    /// Decision #170, AC-06.2 / AC-07.1 / AC-29.2: a name typed with a combining accent is a duplicate of the same name
    /// typed with the precomposed letter. <c>409</c> with the list's name-taken code and message, the name quoted in NFC
    /// (it looks exactly as typed), and only one row has that name.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task Create_with_a_look_alike_of_an_existing_name_is_409(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (precomposed, decomposed) = LookAlikes();
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(manager, list, precomposed)).StatusCode);

        var response = await CreateAsync(manager, list, decomposed);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, TakenCode(list), TakenMessage(list, precomposed));
        Assert.Equal(1, await CountWithKeyAsync(list, precomposed));
    }

    /// <summary>
    /// Decision #170, AC-06.3 / AC-07.3 / AC-29.3 (rename under the create rules): renaming one record to a look-alike of
    /// another record's name is <c>409</c>, and the renamed record keeps its name.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task Rename_to_a_look_alike_of_another_name_is_409(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (precomposed, decomposed) = LookAlikes();
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(manager, list, precomposed)).StatusCode);
        var otherName = Seed.UniqueName("other");
        var other = await IdOfAsync(await CreateAsync(manager, list, otherName));

        var response = await RenameAsync(manager, list, other, decomposed);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.Conflict, TakenCode(list), TakenMessage(list, precomposed));
        Assert.Equal(otherName, (await StoredAsync(list, other)).Name);
    }

    /// <summary>
    /// Decision #170: a decomposed name is stored, returned and keyed in NFC on create. A rename of the record to its
    /// own name in another Unicode form and letter case is allowed (its own name never counts as taken) and is stored in
    /// NFC too.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task Decomposed_name_is_stored_in_NFC_on_create_and_rename(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var (precomposed, decomposed) = LookAlikes();

        var created = await CreateAsync(manager, list, $"  {decomposed}  ");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(precomposed, (await ApiJson.ReadAsync(created)).GetProperty("name").GetString());
        var id = await IdOfAsync(created);
        Assert.Equal((precomposed, precomposed.ToUpperInvariant()), await StoredAsync(list, id));

        // "CAFE" + combining accent + " SUR": the record's own name, decomposed and in upper case.
        var renamed = await RenameAsync(manager, list, id, decomposed.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var upperNfc = precomposed.ToUpperInvariant();
        Assert.Equal(upperNfc, (await ApiJson.ReadAsync(renamed)).GetProperty("name").GetString());
        Assert.Equal((upperNfc, upperNfc), await StoredAsync(list, id));
    }

    /// <summary>
    /// Decision #171, AC-06.2 / AC-07.2 / AC-29.2: a name with no visible character is refused on create with the same
    /// <c>400</c> "Name is required." as an empty name, and nothing is saved.
    /// </summary>
    [Theory]
    [MemberData(nameof(ListsAndInvisibleNames))]
    public async Task Create_with_an_invisible_only_name_is_name_required(string list, string jsonName)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var json = await BodyJsonAsync(manager, list, jsonName);
        var before = await CountAsync(list);

        var response = await manager.PostAsync(PathOf(list), ApiJson.Content(json), Cancel);

        await ApiAssert.ValidationAsync(response, "name", "Name is required.");
        Assert.Equal(before, await CountAsync(list));
    }

    /// <summary>
    /// Decision #171, AC-06.3 / AC-07.3 / AC-29.3: renaming to a name with no visible character is the same <c>400</c>
    /// "Name is required.", and the record keeps its name.
    /// </summary>
    [Theory]
    [MemberData(nameof(ListsAndInvisibleNames))]
    public async Task Rename_to_an_invisible_only_name_is_name_required(string list, string jsonName)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var name = Seed.UniqueName("keep");
        var id = await IdOfAsync(await CreateAsync(manager, list, name));
        var json = await BodyJsonAsync(manager, list, jsonName);

        var response = await manager.PutAsync($"{PathOf(list)}/{id}", ApiJson.Content(json), Cancel);

        await ApiAssert.ValidationAsync(response, "name", "Name is required.");
        Assert.Equal(name, (await StoredAsync(list, id)).Name);
    }

    /// <summary>
    /// Decision #171: invisible characters among visible ones are allowed and kept exactly as sent (a zero width joiner
    /// and a zero width space between words here), on create and on rename. Before T-034 the zero width space was at the
    /// start of the name; decision #176 now refuses it there (see the T-034 tests below).
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task Invisible_characters_among_visible_ones_are_kept(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var withJoiner = $"Caf\u00E9\u200DSur {Suffix()}";
        var withSpace = $"Caf\u00E9\u200B Sur {Suffix()}";

        var created = await CreateAsync(manager, list, withJoiner);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = await IdOfAsync(created);
        Assert.Equal(withJoiner, (await StoredAsync(list, id)).Name);

        var renamed = await RenameAsync(manager, list, id, withSpace);

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(withSpace, (await StoredAsync(list, id)).Name);
    }

    /// <summary>
    /// Design 2.6 with decision #170: the length limit (units 50, items and customers 100, decision #116 code points) is
    /// checked on the stored NFC text. A name of exactly the limit after NFC is accepted although the decomposed text as
    /// sent is longer; one more letter is refused with the length message.
    /// </summary>
    [Theory]
    [InlineData("units", 50)]
    [InlineData("items", 100)]
    [InlineData("customers", 100)]
    public async Task Length_limit_counts_the_name_after_NFC(string list, int limit)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var suffix = Suffix(); // 8 letters, so the rest is limit - 8 decomposed "\u00E9" (2 code points each as sent)
        var atLimit = suffix + string.Concat(Enumerable.Repeat("e\u0301", limit - suffix.Length));
        var overLimit = Suffix() + string.Concat(Enumerable.Repeat("e\u0301", limit - suffix.Length + 1));

        var accepted = await CreateAsync(manager, list, atLimit);
        var refused = await CreateAsync(manager, list, overLimit);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var stored = (await ApiJson.ReadAsync(accepted)).GetProperty("name").GetString()!;
        Assert.Equal(limit, stored.Length); // every "\u00E9" is one UTF-16 unit and one code point once in NFC
        await ApiAssert.ValidationAsync(refused, "name", $"Name must be {limit} characters or fewer.");
    }

    /// <summary>
    /// Decision #170 safety net, first of two cases where <c>string.Normalize</c> would throw: a lone surrogate (half
    /// of an emoji's UTF-16 pair). Such a name never reaches the rule: the JSON reader refuses it with <c>400</c>
    /// <c>validation</c>, so it cannot become a <c>500</c>, and nothing is saved. The second case, U+FFFE, does reach
    /// the rule and is covered by the two tests below (decision #174).
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task Name_with_a_lone_surrogate_is_400_not_500(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var json = await BodyJsonAsync(manager, list, @"Caf\uD800 Sur"); // JSON escape of a high surrogate with no pair
        var before = await CountAsync(list);

        var response = await manager.PostAsync(PathOf(list), ApiJson.Content(json), Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await ApiJson.ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(before, await CountAsync(list));
    }

    /// <summary>
    /// Decision #174 (BUG-002 review), AC-06.2 / AC-07.1 / AC-29.2: a name holding U+FFFE (valid JSON, sent here as
    /// the escape) is refused on create with <c>400</c> "Name contains a character that is not allowed." under
    /// <c>name</c>, not a <c>500</c>, and nothing is saved.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task Create_with_U_FFFE_in_the_name_is_400_not_allowed(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var json = await BodyJsonAsync(manager, list, @"Caf\uFFFE Sur");
        var before = await CountAsync(list);

        var response = await manager.PostAsync(PathOf(list), ApiJson.Content(json), Cancel);

        await ApiAssert.ValidationAsync(response, "name", "Name contains a character that is not allowed.");
        Assert.Equal(before, await CountAsync(list));
    }

    /// <summary>
    /// Decision #174, AC-06.3 / AC-07.3 / AC-29.3: renaming to a name holding U+FFFE is the same <c>400</c>, and the
    /// record keeps its name.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task Rename_with_U_FFFE_in_the_name_is_400_not_allowed(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var name = Seed.UniqueName("keep");
        var id = await IdOfAsync(await CreateAsync(manager, list, name));
        var json = await BodyJsonAsync(manager, list, @"Caf\uFFFE Sur");

        var response = await manager.PutAsync($"{PathOf(list)}/{id}", ApiJson.Content(json), Cancel);

        await ApiAssert.ValidationAsync(response, "name", "Name contains a character that is not allowed.");
        Assert.Equal(name, (await StoredAsync(list, id)).Name);
    }

    // ---- T-034 (SEC-25 fix list, decisions #176 and #181): characters that are not allowed ---------------------------
    //
    // The case lists below are looped over inside one test per list instead of being theory rows: every test signs in a
    // new user (two password hashes), and the tests of one class run one after another, so a row per case made this
    // class the slowest in the suite. ForEachCaseAsync still reports every failing case by name.

    /// <summary>
    /// Names the T-034 rules refuse as "not allowed", as the text after a unique visible part. Written as JSON escapes and
    /// sent raw, so the API receives exactly these code points.
    /// </summary>
    private static readonly string[] NotAllowedEndings =
    [
        @" \u200B",        // zero width space at the end (SEC-25 twin)
        @" \u3164",        // Hangul filler at the end (SEC-25 twin)
        @" \u034F",        // combining grapheme joiner at the end (SEC-25 twin)
        @" \uFE0F",        // variation selector at the end
        @" \u200D",        // zero width joiner at the end, where it joins nothing
        @" \u2800",        // braille blank at the end (SEC-25 twin, decision #182)
        @"\u0000x",        // NUL inside
        @"\tx",            // tab inside
        @"\nx",            // line feed inside
        @"\u0085x",        // NEXT LINE (C1 control) inside
        @"\uFDD0x",        // noncharacter inside
        @"\uFFFFx",        // noncharacter inside
        @"\uD83F\uDFFFx", // U+1FFFF, a noncharacter outside the BMP, as a JSON surrogate pair
    ];

    /// <summary>Names decision #176 counts as blank (Hangul fillers, U+2800), as JSON escapes.</summary>
    private static readonly string[] BlankLookingNames =
    [
        @"\u3164",             // HANGUL FILLER (SEC-25: a blank customer)
        @"\u3164\u3164\u3164",
        @"\u115F\u1160",      // Hangul choseong and jungseong fillers
        @"\uFFA0",             // halfwidth Hangul filler
        @"\u2800",             // braille pattern blank
        @" \u2800\u200B ",    // braille blank, zero width space and spaces
    ];

    /// <summary>
    /// Decision #182: starts the rules refuse as "not allowed", each sent before a unique visible part, as JSON escapes.
    /// </summary>
    private static readonly string[] NotAllowedStarts =
    [
        @"\u0301Deli",  // COMBINING ACUTE ACCENT first (Mn)
        @" \u0301Deli", // the same once the space is trimmed
        @"\u20DDDeli",  // COMBINING ENCLOSING CIRCLE first (Me)
        @"\u2800Deli",  // braille blank first
    ];

    /// <summary>Decision #182: lone combining marks, sent as they are (SEC-25: "a lone combining accent").</summary>
    private static readonly string[] LoneCombiningMarks = [@"\u0301", @"\u20DD", @"\u0301\u0301"];

    /// <summary>Every case of <see cref="NotAllowedStarts"/> with a unique visible part, then the lone marks.</summary>
    private static string[] NotAllowedStartCases() =>
        [.. NotAllowedStarts.Select(start => $"{start} {Suffix()}"), .. LoneCombiningMarks];

    /// <summary>
    /// SEC-25 fix, decision #176, AC-06.2 / AC-07.1 / AC-29.2: a name with an invisible character at the end, or a
    /// control character or noncharacter inside, is refused on create with <c>400</c> "Name contains a character that
    /// is not allowed." under <c>name</c> (the #174 shape), and nothing is saved. One test per list, every case of
    /// <see cref="NotAllowedEndings"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task T034_create_with_a_character_not_allowed_is_400(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);

        await ForEachCaseAsync(NotAllowedEndings, async jsonEnding =>
        {
            var json = await BodyJsonAsync(manager, list, $"Deli {Suffix()}{jsonEnding}");
            var before = await CountAsync(list);

            var response = await manager.PostAsync(PathOf(list), ApiJson.Content(json), Cancel);

            await ApiAssert.ValidationAsync(response, "name", "Name contains a character that is not allowed.");
            Assert.Equal(before, await CountAsync(list));
        });
    }

    /// <summary>
    /// SEC-25 fix, decision #176, AC-06.3 / AC-07.3 / AC-29.3: renaming to such a name is the same <c>400</c>, and the
    /// record keeps its name. One test per list, every case of <see cref="NotAllowedEndings"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task T034_rename_to_a_character_not_allowed_is_400(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var name = Seed.UniqueName("keep");
        var id = await IdOfAsync(await CreateAsync(manager, list, name));

        await ForEachCaseAsync(NotAllowedEndings, async jsonEnding =>
        {
            var json = await BodyJsonAsync(manager, list, $"Deli {Suffix()}{jsonEnding}");

            var response = await manager.PutAsync($"{PathOf(list)}/{id}", ApiJson.Content(json), Cancel);

            await ApiAssert.ValidationAsync(response, "name", "Name contains a character that is not allowed.");
            Assert.Equal(name, (await StoredAsync(list, id)).Name);
        });
    }

    /// <summary>
    /// SEC-25 fix: the twin case from the security review. With "Deli Norte ..." stored, the same name followed by a zero
    /// width space or preceded by one is refused with <c>400</c>, so only one record looks like "Deli Norte ...".
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task T034_twin_with_an_invisible_character_at_either_end_is_refused(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var name = $"Deli Norte {Suffix()}";
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(manager, list, name)).StatusCode);

        var after = await CreateAsync(manager, list, name + "\u200B");
        var before = await CreateAsync(manager, list, "\u200B" + name);

        await ApiAssert.ValidationAsync(after, "name", "Name contains a character that is not allowed.");
        await ApiAssert.ValidationAsync(before, "name", "Name contains a character that is not allowed.");
    }

    /// <summary>
    /// SEC-25 fix, decision #176, AC-06.2 / AC-07.2 / AC-29.2: a name made only of Hangul fillers or U+2800 is "Name is
    /// required." on create, and nothing is saved. One test per list, every case of <see cref="BlankLookingNames"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task T034_create_with_a_blank_looking_name_is_name_required(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);

        await ForEachCaseAsync(BlankLookingNames, async jsonName =>
        {
            var json = await BodyJsonAsync(manager, list, jsonName);
            var before = await CountAsync(list);

            var response = await manager.PostAsync(PathOf(list), ApiJson.Content(json), Cancel);

            await ApiAssert.ValidationAsync(response, "name", "Name is required.");
            Assert.Equal(before, await CountAsync(list));
        });
    }

    /// <summary>
    /// SEC-25 fix, decision #176, AC-06.3 / AC-07.3 / AC-29.3: renaming to such a name is the same <c>400</c>, and the
    /// record keeps its name. One test per list, every case of <see cref="BlankLookingNames"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task T034_rename_to_a_blank_looking_name_is_name_required(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var name = Seed.UniqueName("keep");
        var id = await IdOfAsync(await CreateAsync(manager, list, name));

        await ForEachCaseAsync(BlankLookingNames, async jsonName =>
        {
            var json = await BodyJsonAsync(manager, list, jsonName);

            var response = await manager.PutAsync($"{PathOf(list)}/{id}", ApiJson.Content(json), Cancel);

            await ApiAssert.ValidationAsync(response, "name", "Name is required.");
            Assert.Equal(name, (await StoredAsync(list, id)).Name);
        });
    }

    /// <summary>
    /// Decision #182, AC-06.2 / AC-07.1 / AC-29.2: a name that starts with a combining mark or U+2800, or is only
    /// combining marks, is refused on create with <c>400</c> "Name contains a character that is not allowed." under
    /// <c>name</c>, and nothing is saved. One test per list, every case of <see cref="NotAllowedStartCases"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task T034_create_with_a_start_not_allowed_is_400(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);

        await ForEachCaseAsync(NotAllowedStartCases(), async jsonName =>
        {
            var json = await BodyJsonAsync(manager, list, jsonName);
            var before = await CountAsync(list);

            var response = await manager.PostAsync(PathOf(list), ApiJson.Content(json), Cancel);

            await ApiAssert.ValidationAsync(response, "name", "Name contains a character that is not allowed.");
            Assert.Equal(before, await CountAsync(list));
        });
    }

    /// <summary>
    /// Decision #182, AC-06.3 / AC-07.3 / AC-29.3: renaming to such a name is the same <c>400</c>, and the record keeps
    /// its name. One test per list, every case of <see cref="NotAllowedStartCases"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task T034_rename_to_a_start_not_allowed_is_400(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var name = Seed.UniqueName("keep");
        var id = await IdOfAsync(await CreateAsync(manager, list, name));

        await ForEachCaseAsync(NotAllowedStartCases(), async jsonName =>
        {
            var json = await BodyJsonAsync(manager, list, jsonName);

            var response = await manager.PutAsync($"{PathOf(list)}/{id}", ApiJson.Content(json), Cancel);

            await ApiAssert.ValidationAsync(response, "name", "Name contains a character that is not allowed.");
            Assert.Equal(name, (await StoredAsync(list, id)).Name);
        });
    }

    /// <summary>
    /// Runs <paramref name="check"/> for every case and fails once at the end, listing each case that failed with its
    /// assertion message, so a single test can cover a whole case list without hiding which case broke.
    /// </summary>
    private static async Task ForEachCaseAsync(IEnumerable<string> cases, Func<string, Task> check)
    {
        var failures = new List<string>();
        foreach (var testCase in cases)
        {
            try
            {
                await check(testCase);
            }
            catch (XunitException failure)
            {
                failures.Add($"case \"{testCase}\": {failure.Message}");
            }
        }

        // Assert.Fail with the joined text, not Assert.Empty, which would shorten each message in its output.
        if (failures.Count > 0)
        {
            Assert.Fail(string.Join(Environment.NewLine, failures));
        }
    }

    /// <summary>
    /// Decision #171 kept by #176, and #181: invisible characters between visible ones stay allowed and are stored as
    /// sent on create and rename: a ZWNJ inside a Persian word, a family emoji joined by ZWJ, and (#181) a zero width
    /// space and a right-to-left override in the middle.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task T034_invisible_characters_between_visible_ones_are_still_kept(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var persian = $"\u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645 {Suffix()}";
        var family = $"\U0001F468\u200D\U0001F469\u200D\U0001F467 {Suffix()}";
        var middle = $"Milk\u202Egnp\u200B {Suffix()}";

        var created = await CreateAsync(manager, list, persian);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = await IdOfAsync(created);
        Assert.Equal(persian, (await StoredAsync(list, id)).Name);

        Assert.Equal(HttpStatusCode.OK, (await RenameAsync(manager, list, id, family)).StatusCode);
        Assert.Equal(family, (await StoredAsync(list, id)).Name);

        Assert.Equal(HttpStatusCode.OK, (await RenameAsync(manager, list, id, middle)).StatusCode);
        Assert.Equal(middle, (await StoredAsync(list, id)).Name);
    }

    /// <summary>
    /// Decision #182: combining marks after a base letter, and U+2800 between visible characters, are unaffected and
    /// stored as sent, on create and rename: an enclosing mark at the end, a mark with no precomposed form, and a
    /// Devanagari vowel sign after its consonant.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task T034_combining_marks_after_a_letter_are_kept(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);
        var marks = $"Deli x\u0301 {Suffix()} x\u20DD";
        var devanagari = $"\u0915\u0941\u0932\u2800{Suffix()}";

        var created = await CreateAsync(manager, list, marks);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = await IdOfAsync(created);
        Assert.Equal(marks, (await StoredAsync(list, id)).Name);

        Assert.Equal(HttpStatusCode.OK, (await RenameAsync(manager, list, id, devanagari)).StatusCode);
        Assert.Equal(devanagari, (await StoredAsync(list, id)).Name);
    }

    /// <summary>
    /// Legacy names that break the T-034 rules, as <see cref="string.Format(string, object)"/> patterns ({0} is a unique
    /// suffix): a tab inside and a zero width space at the end (#176); a combining accent first and U+2800 at the end
    /// (#182).
    /// </summary>
    private static readonly string[] LegacyNamePatterns = ["Legacy\t{0}\u200B", "\u0301Legacy {0}\u2800"];

    /// <summary>
    /// Decisions #181 and #182 with #172: a record whose stored name breaks the T-034 rules (saved before them, so not
    /// rewritten) can still be edited. A <c>PUT</c> that sends the stored name back unchanged answers <c>200</c> and keeps
    /// the name and its key; for an item it changes the description and for a customer the notes, the case the decision
    /// is for. One test per list, every pattern of <see cref="LegacyNamePatterns"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task T034_put_with_the_unchanged_stored_name_passes(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);

        await ForEachCaseAsync(LegacyNamePatterns, async pattern =>
        {
            var (id, legacy) = await RecordWithLegacyNameAsync(manager, list, pattern);

            var response = await manager.PutAsJsonAsync(
                $"{PathOf(list)}/{id}", await EditBodyAsync(manager, list, id, legacy), Cancel);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ApiJson.ReadAsync(response);
            Assert.Equal(legacy, body.GetProperty("name").GetString());
            Assert.Equal((legacy, NameNormalizer.Normalize(legacy)), await StoredAsync(list, id));
            if (list == "items")
            {
                Assert.Equal("edited", body.GetProperty("description").GetString());
            }
            else if (list == "customers")
            {
                Assert.Equal("edited", body.GetProperty("notes").GetString());
            }
        });
    }

    /// <summary>
    /// Decision #181: once the name really changes, every rule applies. Changing only the letter case of the legacy name
    /// (a rename, as in #179) or adding to it is refused with <c>400</c>, and the stored name is left as it was. One test
    /// per list, every pattern of <see cref="LegacyNamePatterns"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task T034_put_that_changes_a_legacy_name_follows_the_rules(string list)
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager);

        await ForEachCaseAsync(LegacyNamePatterns, async pattern =>
        {
            var (id, legacy) = await RecordWithLegacyNameAsync(manager, list, pattern);

            var upperCase = await manager.PutAsJsonAsync(
                $"{PathOf(list)}/{id}", await EditBodyAsync(manager, list, id, legacy.ToUpperInvariant()), Cancel);
            var longer = await manager.PutAsJsonAsync(
                $"{PathOf(list)}/{id}", await EditBodyAsync(manager, list, id, legacy + "x"), Cancel);

            await ApiAssert.ValidationAsync(upperCase, "name", "Name contains a character that is not allowed.");
            await ApiAssert.ValidationAsync(longer, "name", "Name contains a character that is not allowed.");
            Assert.Equal(legacy, (await StoredAsync(list, id)).Name);
        });
    }

    /// <summary>
    /// Creates a record with a valid name, then writes a name made from <paramref name="pattern"/>, one the T-034 rules
    /// refuse, straight into the database, as a record saved before T-034 would have it (decision #172: such rows are
    /// not rewritten). Returns the id and that name.
    /// </summary>
    private async Task<(int Id, string Legacy)> RecordWithLegacyNameAsync(HttpClient manager, string list, string pattern)
    {
        var id = await IdOfAsync(await CreateAsync(manager, list, Seed.UniqueName("legacy")));
        var legacy = string.Format(pattern, Suffix());
        var key = NameNormalizer.Normalize(legacy);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var updated = list switch
        {
            "units" => await db.Units.Where(u => u.Id == id).ExecuteUpdateAsync(
                set => set.SetProperty(u => u.Name, legacy).SetProperty(u => u.NameNormalized, key), Cancel),
            "items" => await db.Items.Where(i => i.Id == id).ExecuteUpdateAsync(
                set => set.SetProperty(i => i.Name, legacy).SetProperty(i => i.NameNormalized, key), Cancel),
            _ => await db.Customers.Where(c => c.Id == id).ExecuteUpdateAsync(
                set => set.SetProperty(c => c.Name, legacy).SetProperty(c => c.NameNormalized, key), Cancel),
        };
        Assert.Equal(1, updated);
        return (id, legacy);
    }

    /// <summary>
    /// The full <c>PUT</c> body that keeps everything but the name and an edited text field: an item keeps its type and
    /// unit and gets the description "edited"; a customer gets the notes "edited"; a unit has only its name.
    /// </summary>
    private async Task<object> EditBodyAsync(HttpClient manager, string list, int id, string name)
    {
        if (list != "items")
        {
            return list == "customers" ? new { name, notes = "edited" } : new { name };
        }

        var item = await ApiJson.ReadAsync(await manager.GetAsync($"{PathOf(list)}/{id}", Cancel));
        return new
        {
            name,
            description = "edited",
            type = item.GetProperty("type").GetString(),
            unitId = item.GetProperty("unitId").GetInt32(),
        };
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    /// <summary>A unique name in its two Unicode forms: precomposed "é" (U+00E9) and "e" + U+0301.</summary>
    private static (string Precomposed, string Decomposed) LookAlikes()
    {
        var suffix = Suffix();
        return ($"Caf\u00E9 Sur {suffix}", $"Cafe\u0301 Sur {suffix}");
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static string PathOf(string list) => $"/api/v1/{list}";

    private static string TakenCode(string list) => list switch
    {
        "units" => "unit-name-taken",
        "items" => "item-name-taken",
        _ => "customer-name-taken",
    };

    private static string TakenMessage(string list, string name) => list switch
    {
        "units" => $"A unit named '{name}' already exists.",
        "items" => $"An item named '{name}' already exists.",
        _ => $"A customer named '{name}' already exists.",
    };

    /// <summary>POST a new record named <paramref name="name"/>; an item gets a new unit and the RawMaterial type.</summary>
    private async Task<HttpResponseMessage> CreateAsync(HttpClient manager, string list, string name) =>
        await manager.PostAsJsonAsync(PathOf(list), await BodyAsync(manager, list, name), Cancel);

    /// <summary>
    /// PUT the record's full body with a new name. An item gets a fresh unit, which is allowed because nothing uses the
    /// item yet (AC-07.3).
    /// </summary>
    private async Task<HttpResponseMessage> RenameAsync(HttpClient manager, string list, int id, string name) =>
        await manager.PutAsJsonAsync($"{PathOf(list)}/{id}", await BodyAsync(manager, list, name), Cancel);

    private static async Task<object> BodyAsync(HttpClient manager, string list, string name) => list switch
    {
        "items" => new { name, type = "RawMaterial", unitId = (await Seed.CreateUnitAsync(manager)).Id },
        _ => new { name },
    };

    /// <summary>
    /// A raw JSON body whose name is <paramref name="jsonName"/> as written (JSON escapes kept), every other field valid,
    /// so the only problem in the request is the name.
    /// </summary>
    private static async Task<string> BodyJsonAsync(HttpClient manager, string list, string jsonName) => list switch
    {
        "items" => $$"""{ "name": "{{jsonName}}", "type": "RawMaterial", "unitId": {{(await Seed.CreateUnitAsync(manager)).Id}} }""",
        _ => $$"""{ "name": "{{jsonName}}" }""",
    };

    private static async Task<int> IdOfAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ApiJson.ReadAsync(response)).GetProperty("id").GetInt32();
    }

    /// <summary>The stored Name and NameNormalized of one record, read straight from the database.</summary>
    private async Task<(string Name, string Normalized)> StoredAsync(string list, int id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = list switch
        {
            "units" => await db.Units.Where(u => u.Id == id).Select(u => new { u.Name, u.NameNormalized }).SingleAsync(Cancel),
            "items" => await db.Items.Where(i => i.Id == id).Select(i => new { i.Name, i.NameNormalized }).SingleAsync(Cancel),
            _ => await db.Customers.Where(c => c.Id == id).Select(c => new { c.Name, c.NameNormalized }).SingleAsync(Cancel),
        };
        return (row.Name, row.NameNormalized);
    }

    /// <summary>How many records of the list have the comparison key of <paramref name="name"/>.</summary>
    private async Task<int> CountWithKeyAsync(string list, string name)
    {
        var key = NameNormalizer.Normalize(name);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return list switch
        {
            "units" => await db.Units.CountAsync(u => u.NameNormalized == key, Cancel),
            "items" => await db.Items.CountAsync(i => i.NameNormalized == key, Cancel),
            _ => await db.Customers.CountAsync(c => c.NameNormalized == key, Cancel),
        };
    }

    private async Task<int> CountAsync(string list)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return list switch
        {
            "units" => await db.Units.CountAsync(Cancel),
            "items" => await db.Items.CountAsync(Cancel),
            _ => await db.Customers.CountAsync(Cancel),
        };
    }
}
