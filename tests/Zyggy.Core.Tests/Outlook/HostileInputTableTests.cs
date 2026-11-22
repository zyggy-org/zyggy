using Zyggy.Core.Dream;
using Zyggy.Core.Outlook;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>
/// Spec 58 AC-9 and D-Q18 (verify 01M81J9G3WE85TQ5H04GMA0B7Y (4)): an adversarial table against the work laptop's sensitive-data
/// filter and the dream's private-detail strip — national register numbers, sensitive terms, attachment names, phone numbers, e-mail
/// and postal addresses, in Belgian formats in French, Dutch and English, with international prefixes, separators, labels, line
/// breaks and invisible format characters. Every hostile row must be caught; the ordinary rows must pass untouched.
/// </summary>
public sealed class HostileInputTableTests
{
    private static readonly WorkMailFilter Filter = WorkMailFilter.Parse("patient\ndiagnose\nmedisch dossier\nhôpital\nkanker\ncancer\n");

    /// <summary>Gets texts holding a national register number or a sensitive term: the mail is kept as sensitive.</summary>
    public static TheoryData<string> Sensitive => new()
    {
        // National register numbers (85.07.30-033.28 and the 2000+ form 01.02.03-123.45 are valid).
        "RRN:85073003328",
        "NN85.07.30-033.28",
        "rijksregisternr. 85.07.30-033.28",
        "n° national : 85 07 30-033 28",
        "INSZ 01.02.03-123.45 on file",
        "85.07.30\n033.28",
        "85.07.30\r\n033.28",
        "85­07­30­033­28",
        "85.07.30⁠-033.28",
        "85‎07‏30‪033‬28",
        "８５０７３０-０３３２８",
        "85 07 30 033 28",
        "85 07 30 033 28",
        "(85.07.30-033.28)",
        "ref85073003328x",

        // Sensitive terms.
        "Le PATIËNT est arrivé",
        "de pati­ënten wachten",
        "RE: Diagnose:",
        "the diagnoses\r\nare in",
        "het medisch dossier",
        "het medisch\n\ndossier",
        "het medisch-dossier",
        "transfert vers l’HÔPITAL",
        "un suivi cancéreux",
        "pa​tient",
        "ｐａｔｉｅｎｔ",
        "Patiënt",
        "kanker⁠behandeling",
        "﻿Diag⁠nose",
    };

    /// <summary>Gets facts that name the attachment <c>Offerte_Klant-2026.pdf</c>: the mail is kept in doubt.</summary>
    public static TheoryData<string> NamesAttachment => new()
    {
        "See Offerte Klant 2026",
        "see offerte_klant-2026.PDF",
        "see OFFERTE–KLANT–2026",
        "see offerte\nklant\n2026",
        "see Offerte Klant 2026.pdf for prices",
        "the ｏｆｆｅｒｔｅ klant 2026",
        "see offer​te klant 2026",
        "see Offerte­_Klant 2026",
    };

    /// <summary>Gets texts with a private detail, and a fragment of it that must not survive the strip.</summary>
    public static TheoryData<string, string> PrivateDetail => new()
    {
        // Phone numbers.
        { "Bel T.0475 12 34 56 aub", "12 34 56" },
        { "GSM: 0475/12.34.56", "12.34.56" },
        { "Tel +32 (0)2 123 45 67", "123 45 67" },
        { "Tél.+32 2 123 45 67", "123 45 67" },
        { "tel:+32475123456", "475123456" },
        { "Mobile: +32 475 12 34 56", "475" },
        { "Call 0475\n12 34 56 today", "12 34 56" },
        { "Call 0475­12­34­56", "0475" },
        { "UK office +44 20 7946 0958", "7946 0958" },
        { "US office +1 (555) 123-4567", "123-4567" },
        { "Bel (0475) 12 34 56", "12 34 56" },
        { "Tél. (02) 123 45 67", "123 45 67" },
        { "Call 0032-475-12-34-56", "12-34-56" },
        { "Telefoon:0475 12 34 56", "12 34 56" },
        { "GSM0475123456", "0475123456" },
        { "Call +32—2—123—45—67", "123—45—67" },
        { "Bel +32 (0) 475 12 34 56", "12 34 56" },
        { "Call +32\n475 12 34 56", "12 34 56" },
        { "Bel ０４７５ １２ ３４ ５６", "５６" },
        { "Fax: 02/123.45.68", "123.45.68" },

        // Phone numbers that start like a date (verify 01M81K807RE27PQS6J4N961QBD (1), (4)).
        { "Bel 06.12.34.56.78", "56.78" },
        { "Bel 04.75.12.34.56", "12.34.56" },
        { "Op 05/12/2026 0475 12 34 56 bellen", "12 34 56" },
        { "Bel 02.12.34.56.78", "56.78" },
        { "Bel 09/12/34 56 78", "56 78" },
        { "Bel (02.12.34.56.78)", "56.78" },
        { "Bel 01.01.01.01.01", "01.01.01" },
        { "Bel 02/12.34.56.78", "56.78" },
        { "05-12-2026-0475123456", "0475123456" },
        { "31.12.99 0475/12.34.56", "12.34.56" },
        { "Bel 03-12-34-56-78", "56-78" },
        { "Tel 01/12/2026 - 0475 12 34 56", "12 34 56" },
        { "Bel 0475 12 34 56 op 05/12/2026", "12 34 56" },
        { "Tel 05.12.2026 09 0475 12 34 56", "12 34 56" },
        { "Bel 01/12/2026-31/12/2026 0475123456", "0475123456" },
        { "Tel 08.12.34.56.78", "56.78" },

        // Archive file names (verify 01M81K807RE27PQS6J4N961QBD (2)).
        { "offerte-0475123456", "0475123456" },

        // E-mail addresses.
        { "Mail:jan@example.test", "example.test" },
        { "JAN.PEETERS@EXAMPLE.TEST", "EXAMPLE" },
        { "écrire à jän.péeters@exämple.test", "exämple" },
        { "jan­.peeters@example.test", "example.test" },
        { "jan@sub.example.co.uk", "example.co.uk" },
        { "<jan_peeters@example.test>", "jan_peeters" },
        { "mailto:jan@example.test", "example.test" },
        { "jan.peeters @ example.test", "example.test" },

        // Postal addresses.
        { "Kerkstraat 12A, 2000 Antwerpen", "Kerkstraat 12A" },
        { "Stationsstraat 12 bus 3, 9000 Gent", "12 bus 3" },
        { "Lange Lozanastraat 14/3", "14/3" },
        { "Avenue Louise 54 boîte 3, 1050 Ixelles", "boîte 3" },
        { "Chaussée de Waterloo 1234", "Waterloo 1234" },
        { "Grote Markt 1, 1000 Brussel", "Markt 1" },
        { "rue Neuve 12, B-1000 Bruxelles", "B-1000" },
        { "Kerkstraat nr. 12", "nr. 12" },
        { "rue de la Loi, 16", "Loi, 16" },
        { "221B Baker Street", "221B" },
        { "10 Downing Street, London", "Downing" },
        { "Kerk­straat 12", "12" },
        { "Kerkstraat\n12", "12" },
        { "Place Flagey 18", "Flagey 18" },
        { "Boulevard Anspach 1 bte 5", "bte 5" },
        { "Rue du Marché-aux-Herbes 61", "Herbes 61" },
        { "St.-Pietersnieuwstraat 25", "25" },
        { "Sint-Jansvliet 3", "vliet 3" },
        { "Rue de la Loi 16\n1000 Bruxelles", "Bruxelles" },
        { "Kortrijksesteenweg 1100", "1100" },
        { "Kerkstraat n° 12", "n° 12" },
    };

    /// <summary>Gets ordinary business text the strip leaves alone.</summary>
    public static TheoryData<string> Ordinary => new()
    {
        "The meeting of 01/02/2026 at 09:00 moved to 2026-11-21.",
        "Total 1 234,00 EUR for 12 licences, PO 4500123456.",
        "Invoice 2026-001234 is due; ticket INC0012345 is closed.",
        "The price @ 10.30 was agreed.",
        "Jan Peeters, project lead at Acme, asks to confirm the Q4 budget by Friday.",
        "On January 12, 2026 Jan called about the Kerkstraat project.",
        "Version 2.10.3 shipped on 22.11.2026.",

        // Dates, alone, as a range or with a time (verify 01M81K807RE27PQS6J4N961QBD (1), (4)).
        "Delivered 05/12/2026.",
        "Meeting 05/12/2026 09:00 in room 3.",
        "Meeting 05/12/2026 09.30 in room 3.",
        "Period 01/12/2026-31/12/2026 closed.",
        "Period 01.12.2026 - 31.12.2026 closed.",
        "Due 05.12.26, then 06.01.27.",
        "Signed 01-02-2026 (copy 2).",
        "Starts 05/12 at 9h.",
    };

    [Theory]
    [MemberData(nameof(Sensitive))]
    public void Check_HostileSensitiveText_KeptSensitive(string text)
    {
        // Act
        var verdict = Filter.Check(Mail(body: text), ["Asks to confirm the Q4 budget"], doubt: false);

        // Assert
        verdict.Should().Be(WorkMailVerdict.KeepSensitive);
    }

    [Theory]
    [MemberData(nameof(NamesAttachment))]
    public void Check_HostileFactNamingAttachment_KeptInDoubt(string fact)
    {
        // Act
        var verdict = Filter.Check(Mail(body: "Hello", attachments: ["Offerte_Klant-2026.pdf"]), [fact], doubt: false);

        // Assert
        verdict.Should().Be(WorkMailVerdict.KeepInDoubt);
    }

    [Theory]
    [MemberData(nameof(PrivateDetail))]
    public void Strip_HostilePrivateDetail_Removed(string text, string fragment)
    {
        // Act
        var stripped = PrivateDetails.Strip(text);

        // Assert
        stripped.Should().NotContain(fragment).And.Contain(PrivateDetails.Removed);
    }

    [Theory]
    [MemberData(nameof(Ordinary))]
    public void Strip_OrdinaryText_Kept(string text)
    {
        // Act
        var stripped = PrivateDetails.Strip(text);

        // Assert
        stripped.Should().Be(text);
    }

    private static OutlookMail Mail(string? body, string[]? attachments = null) =>
        new(
            new OutlookMailSummary("m1", "Sender m1", "m1@example.test", "Budget", "2026-11-21T09:00:00+01:00", "normal", "none", false, (attachments ?? []).Length > 0, body is null),
            attachments ?? [],
            body,
            false);
}
