using System.Text.RegularExpressions;

namespace ImmichDesktop.Core.Privacy;

/// <summary>Kinds of private things a picture can show.</summary>
[Flags]
public enum SensitiveKind
{
    None = 0,
    Explicit = 1 << 0,     // nudity or sex
    Suggestive = 1 << 1,   // revealing, but not explicit
    PaymentCard = 1 << 2,
    IdDocument = 1 << 3,   // passport, driver's license, ID card
    Financial = 1 << 4,    // bank details, statements, tax forms
    Credentials = 1 << 5,  // passwords, recovery codes, keys
    Medical = 1 << 6,
}

/// <summary>
/// Looks through text read from a picture for signs it shows something private: card numbers
/// (checked with the card checksum), IDs, bank details, passwords, medical papers. Strong signs
/// count alone; weaker words need company, to keep false alarms down.
/// </summary>
public static partial class SensitiveText
{
    public static SensitiveKind Find(IEnumerable<string> lines)
    {
        var text = string.Join("\n", lines);
        var upper = text.ToUpperInvariant();
        var found = SensitiveKind.None;

        // Receipts mention "VISA" and "DEBIT" too, so words alone need something only a card face has.
        if (HasCardNumber(text) || Count(upper, CardBrands) >= 1 && Count(upper, CardFace) >= 1)
            found |= SensitiveKind.PaymentCard;
        if (Mrz().IsMatch(upper.Replace(" ", "")) || Count(upper, IdStrong) >= 1 && Count(upper, IdWeak) >= 1 || Count(upper, IdWeak) >= 3)
            found |= SensitiveKind.IdDocument;
        if (Ssn().IsMatch(text) && Count(upper, ["SOCIAL SECURITY", "SSN", "SOC SEC"]) >= 1 || Count(upper, ["SOCIAL SECURITY NUMBER", "SOCIAL SECURITY CARD"]) >= 1)
            found |= SensitiveKind.IdDocument;
        if (HasIban(upper) || Count(upper, FinancialStrong) >= 1 || Count(upper, FinancialWeak) >= 3)
            found |= SensitiveKind.Financial;
        if (Count(upper, CredentialStrong) >= 1 || Count(upper, CredentialWeak) >= 2 || HasSeedPhrase(text))
            found |= SensitiveKind.Credentials;
        if (Count(upper, MedicalStrong) >= 1 || Count(upper, MedicalWeak) >= 3)
            found |= SensitiveKind.Medical;
        return found;
    }

    static readonly string[] CardBrands = ["VISA", "MASTERCARD", "AMERICAN EXPRESS", "AMEX", "DISCOVER", "MAESTRO", "DEBIT", "CREDIT"];
    static readonly string[] CardFace = ["VALID THRU", "VALID THROUGH", "GOOD THRU", "EXPIRES END", "VALID FROM", "MEMBER SINCE", "CVV", "CVC", "CARDHOLDER", "AUTHORIZED SIGNATURE"];
    static readonly string[] IdStrong = ["PASSPORT", "PASSEPORT", "REISEPASS", "PASAPORTE", "DRIVER LICENSE", "DRIVER'S LICENSE", "DRIVERS LICENSE", "DRIVER LICENCE", "DRIVING LICENCE", "IDENTITY CARD", "NATIONAL ID", "IDENTIFICATION CARD", "PERSONALAUSWEIS", "CARTE D'IDENTITE", "CARTE NATIONALE", "RESIDENCE PERMIT", "PERMANENT RESIDENT"];
    static readonly string[] IdWeak = ["DATE OF BIRTH", "DOB", "SEX", "NATIONALITY", "EXPIRES", "EXP", "ISS", "ISSUED", "DATE OF ISSUE", "SURNAME", "GIVEN NAMES", "CLASS", "RESTRICTIONS", "ENDORSEMENTS", "HGT", "EYES", "DONOR"];
    static readonly string[] FinancialStrong = ["ACCOUNT NUMBER", "ROUTING NUMBER", "SORT CODE", "SWIFT", "BIC", "IBAN", "W-2", "FORM 1099", "TAX RETURN", "1040", "PAYSLIP", "PAY STUB", "PAYSTUB"];
    static readonly string[] FinancialWeak = ["STATEMENT", "BALANCE", "ACCOUNT", "DEPOSIT", "WITHDRAWAL", "TRANSACTION", "BANK", "SALARY", "NET PAY", "GROSS PAY", "AVAILABLE BALANCE", "OVERDRAFT", "INTEREST"];
    static readonly string[] CredentialStrong = ["RECOVERY CODE", "BACKUP CODE", "RECOVERY KEY", "SEED PHRASE", "RECOVERY PHRASE", "SECRET RECOVERY", "PRIVATE KEY", "MNEMONIC", "WIFI PASSWORD", "WI-FI PASSWORD", "BEGIN RSA", "BEGIN OPENSSH"];
    static readonly string[] CredentialWeak = ["PASSWORD", "USERNAME", "PIN", "PASSCODE", "SECURITY CODE", "VERIFICATION CODE", "2FA", "TWO-FACTOR", "AUTHENTICATOR", "SECRET"];
    static readonly string[] MedicalStrong = ["PRESCRIPTION", "DIAGNOSIS", "MEDICAL RECORD", "LAB RESULTS", "LABORATORY REPORT", "PATHOLOGY", "DISCHARGE SUMMARY", "HEALTH INSURANCE CARD", "MEDICARE", "MEDICAID"];
    static readonly string[] MedicalWeak = ["PATIENT", "DOCTOR", "PHYSICIAN", "CLINIC", "HOSPITAL", "MEDICATION", "DOSAGE", "RX", "MG", "INSURANCE", "MEMBER ID", "TREATMENT"];

    /// <summary>How many of the words appear as whole words (plurals count).</summary>
    static int Count(string upper, IEnumerable<string> words) =>
        words.Count(w => Regex.IsMatch(upper, $@"(?<![A-Z0-9]){Regex.Escape(w)}S?(?![A-Z0-9])"));

    /// <summary>13–19 digits (spaces or dashes between groups) that pass the card checksum.</summary>
    internal static bool HasCardNumber(string text)
    {
        foreach (Match m in CardCandidate().Matches(text))
        {
            var digits = new string(m.Value.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length is >= 13 and <= 19 && digits.Distinct().Count() > 1 && Luhn(digits))
                return true;
        }
        return false;
    }

    internal static bool Luhn(string digits)
    {
        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt && (d *= 2) > 9)
                d -= 9;
            sum += d;
            doubleIt = !doubleIt;
        }
        return sum % 10 == 0;
    }

    /// <summary>A crypto wallet seed: a numbered list of 12 or 24 short lowercase words.</summary>
    static bool HasSeedPhrase(string text)
    {
        var numbered = SeedWord().Matches(text).Count;
        return numbered >= 12;
    }

    [GeneratedRegex(@"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)")]
    private static partial Regex CardCandidate();

    // Machine-readable zone of passports and ID cards: long runs of letters, digits and '<' fillers.
    [GeneratedRegex(@"[A-Z0-9<]{25,}<<[A-Z0-9<]*")]
    private static partial Regex Mrz();

    [GeneratedRegex(@"(?<!\d)\d{3}-\d{2}-\d{4}(?!\d)")]
    private static partial Regex Ssn();

    /// <summary>An IBAN (bank account number), checked with its mod-97 checksum.</summary>
    internal static bool HasIban(string upper)
    {
        foreach (Match m in Iban().Matches(upper))
        {
            var iban = m.Value.Replace(" ", "");
            if (iban.Length is < 15 or > 34)
                continue;
            var rearranged = iban[4..] + iban[..4];
            var remainder = 0;
            foreach (var c in rearranged)
            {
                var value = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10;
                remainder = value >= 10 ? (remainder * 100 + value) % 97 : (remainder * 10 + value) % 97;
            }
            if (remainder == 1)
                return true;
        }
        return false;
    }

    // Two letters, two check digits, then groups (often written in fours with spaces).
    [GeneratedRegex(@"(?<![A-Z0-9])[A-Z]{2}\d{2}(?: ?[A-Z0-9]{2,4}){3,8}(?![A-Z0-9])")]
    private static partial Regex Iban();

    [GeneratedRegex(@"(?<![\w])(?:[1-9]|1\d|2[0-4])[.)]\s*[a-z]{3,8}(?![\w])")]
    private static partial Regex SeedWord();
}
