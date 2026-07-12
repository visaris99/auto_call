namespace Core;

public static class UpdateTrust
{
    public const string ActiveKeyId = "58ee66c991445856";

    private const string ActivePublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA68LV+0AZJNtLnGHTni8b
        HJd4dqUYP/+tvHayMbajAuFaAgh/he7/ErV1peIcfWk/uBgOVTfLDGipG6GnwiTz
        voW9APwBnWRItay1NpMvRViBkVDdW9pOQdHxHMKo4rQaFVOJHbye4Sfaogh1aygY
        /PJeH7xOZ++fCcRU08VEAH6R5bJtuINCIK6a6EqxEm0Bo9IXjZiDMb5SCGolpc6Y
        ww5H6f9A1fNKatSw79ASu0G2NPLGmwQb/TxpWd6+gH21e7VLe4Dt7B6IY0s8jIAa
        3E/DSV/NIn8AFtAVklfMQvNJUhNrp1S/2CmtHqHyjrgH2m3Yrykup7RIaxYyPQt9
        KaVTGGvTfuUH+xtRYsgY3q19nJriLsI6WJpxDqgfpdgQD7/p1uGOu/ff8KOOvrKg
        ZE4jUIeSsNJK3R3yd/ryZQzeFUq+8ov74G4H1NskAcbWZYxv4pDo421KmynqMN4Q
        m0c9/KeRqrX15rEv+LboejLKKwyyEOHrVUCExiD1Ap6pAgMBAAE=
        -----END PUBLIC KEY-----
        """;

    private static readonly IReadOnlyDictionary<string, string> PublicKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ActiveKeyId] = ActivePublicKey,
        };

    public static IReadOnlyDictionary<string, string> TrustedPublicKeys => PublicKeys;
}
