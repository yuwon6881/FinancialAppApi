namespace FinancialAppApi.Services.Investments;

public sealed record CurrencyCatalogItem(string Code, string Symbol, string Name, string Label);

/// <summary>
/// Versioned, runtime-independent ISO 4217 catalog. Keep retired/fund/test codes out of this list.
/// Symbols are deliberately disambiguated where the common glyph is shared by several currencies.
/// </summary>
public static class CurrencyCatalog
{
    public const string Version = "iso-4217-active-2026-01";

    private static readonly IReadOnlyList<CurrencyCatalogItem> ItemsValue = Parse("""
AED|د.إ|UAE Dirham
AFN|؋|Afghani
ALL|L|Lek
AMD|֏|Armenian Dram
ANG|NAƒ|Netherlands Antillean Guilder
AOA|Kz|Kwanza
ARS|AR$|Argentine Peso
AUD|A$|Australian Dollar
AWG|Afl.|Aruban Florin
AZN|₼|Azerbaijan Manat
BAM|KM|Convertible Mark
BBD|Bds$|Barbados Dollar
BDT|৳|Taka
BGN|лв|Bulgarian Lev
BHD|د.ب|Bahraini Dinar
BIF|FBu|Burundi Franc
BMD|BD$|Bermudian Dollar
BND|B$|Brunei Dollar
BOB|Bs.|Boliviano
BRL|R$|Brazilian Real
BSD|B$|Bahamian Dollar
BTN|Nu.|Ngultrum
BWP|P|Pula
BYN|Br|Belarusian Ruble
BZD|BZ$|Belize Dollar
CAD|C$|Canadian Dollar
CDF|FC|Congolese Franc
CHF|CHF|Swiss Franc
CLP|CL$|Chilean Peso
CNY|CN¥|Yuan Renminbi
COP|CO$|Colombian Peso
CRC|₡|Costa Rican Colon
CUP|CU$|Cuban Peso
CVE|Esc|Cabo Verde Escudo
CZK|Kč|Czech Koruna
DJF|Fdj|Djibouti Franc
DKK|kr|Danish Krone
DOP|RD$|Dominican Peso
DZD|دج|Algerian Dinar
EGP|E£|Egyptian Pound
ERN|Nfk|Nakfa
ETB|Br|Ethiopian Birr
EUR|€|Euro
FJD|FJ$|Fiji Dollar
FKP|FK£|Falkland Islands Pound
GBP|£|Pound Sterling
GEL|₾|Lari
GHS|GH₵|Ghana Cedi
GIP|GI£|Gibraltar Pound
GMD|D|Dalasi
GNF|FG|Guinean Franc
GTQ|Q|Quetzal
GYD|G$|Guyana Dollar
HKD|HK$|Hong Kong Dollar
HNL|L|Lempira
HTG|G|Gourde
HUF|Ft|Forint
IDR|Rp|Rupiah
ILS|₪|New Israeli Sheqel
INR|₹|Indian Rupee
IQD|ع.د|Iraqi Dinar
IRR|﷼|Iranian Rial
ISK|kr|Iceland Krona
JMD|J$|Jamaican Dollar
JOD|د.ا|Jordanian Dinar
JPY|¥|Yen
KES|KSh|Kenyan Shilling
KGS|с|Som
KHR|៛|Riel
KMF|CF|Comorian Franc
KPW|₩|North Korean Won
KRW|₩|Won
KWD|د.ك|Kuwaiti Dinar
KYD|CI$|Cayman Islands Dollar
KZT|₸|Tenge
LAK|₭|Lao Kip
LBP|L£|Lebanese Pound
LKR|Rs|Sri Lanka Rupee
LRD|L$|Liberian Dollar
LSL|L|Loti
LYD|ل.د|Libyan Dinar
MAD|د.م.|Moroccan Dirham
MDL|L|Moldovan Leu
MGA|Ar|Malagasy Ariary
MKD|ден|Denar
MMK|K|Kyat
MNT|₮|Tugrik
MOP|MOP$|Pataca
MRU|UM|Ouguiya
MUR|₨|Mauritius Rupee
MVR|Rf|Rufiyaa
MWK|MK|Malawi Kwacha
MXN|MX$|Mexican Peso
MYR|RM|Malaysian Ringgit
MZN|MT|Mozambique Metical
NAD|N$|Namibia Dollar
NGN|₦|Naira
NIO|C$|Cordoba Oro
NOK|kr|Norwegian Krone
NPR|रू|Nepalese Rupee
NZD|NZ$|New Zealand Dollar
OMR|ر.ع.|Rial Omani
PAB|B/.|Balboa
PEN|S/|Sol
PGK|K|Kina
PHP|₱|Philippine Peso
PKR|₨|Pakistan Rupee
PLN|zł|Zloty
PYG|₲|Guarani
QAR|ر.ق|Qatari Rial
RON|lei|Romanian Leu
RSD|дин.|Serbian Dinar
RUB|₽|Russian Ruble
RWF|FRw|Rwanda Franc
SAR|ر.س|Saudi Riyal
SBD|SI$|Solomon Islands Dollar
SCR|₨|Seychelles Rupee
SDG|ج.س.|Sudanese Pound
SEK|kr|Swedish Krona
SGD|S$|Singapore Dollar
SHP|SH£|Saint Helena Pound
SLE|Le|Leone
SOS|S|Somali Shilling
SRD|SR$|Surinam Dollar
SSP|SS£|South Sudanese Pound
STN|Db|Dobra
SVC|₡|El Salvador Colon
SYP|LS|Syrian Pound
SZL|L|Lilangeni
THB|฿|Baht
TJS|SM|Somoni
TMT|m|Turkmenistan New Manat
TND|د.ت|Tunisian Dinar
TOP|T$|Pa'anga
TRY|₺|Turkish Lira
TTD|TT$|Trinidad and Tobago Dollar
TWD|NT$|New Taiwan Dollar
TZS|TSh|Tanzanian Shilling
UAH|₴|Hryvnia
UGX|USh|Uganda Shilling
USD|US$|US Dollar
UYU|$U|Peso Uruguayo
UZS|сўм|Uzbekistan Sum
VED|Bs.D|Bolívar Digital
VES|Bs.S|Bolívar Soberano
VND|₫|Dong
VUV|VT|Vatu
WST|WS$|Tala
XAF|FCFA|CFA Franc BEAC
XCD|EC$|East Caribbean Dollar
XOF|CFA|CFA Franc BCEAO
XPF|CFPF|CFP Franc
YER|﷼|Yemeni Rial
ZAR|R|Rand
ZMW|ZK|Zambian Kwacha
ZWG|ZiG|Zimbabwe Gold
""");

    private static readonly HashSet<string> Codes = ItemsValue
        .Select(value => value.Code)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<CurrencyCatalogItem> Items => ItemsValue;

    public static bool Contains(string? code)
        => !string.IsNullOrWhiteSpace(code) && Codes.Contains(code.Trim());

    private static IReadOnlyList<CurrencyCatalogItem> Parse(string source) =>
        source.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|', 3))
            .Select(parts => new CurrencyCatalogItem(parts[0], parts[1], parts[2], $"{parts[0]} ({parts[1]})"))
            .OrderBy(value => value.Code, StringComparer.Ordinal)
            .ToArray();
}
