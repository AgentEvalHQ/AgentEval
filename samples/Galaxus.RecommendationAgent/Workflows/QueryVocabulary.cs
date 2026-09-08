// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Text;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>
/// THE §0.5 / D-3 control: a STRUCTURAL constraint on which words may reach query generation.
/// </summary>
/// <remarks>
/// <para>
/// <b>The threat, stated plainly.</b> The coverage reviewer is allowed to propose a new interest
/// from a review snippet, and that interest's <see cref="Interest.QueryTerms"/> drive the next
/// round's retrieval. Review text is written by customers and by marketplace sellers — Galaxus
/// takes roughly four thousand user-authored ratings a day. So a seller can write steering text,
/// the reviewer proposes the interest, discovery runs the injected query, the seller's SKU comes
/// back through <i>legitimate</i> retrieval, it is therefore genuinely in the candidate set, and
/// every containment check downstream stays green. The grounding story is sound and it cannot
/// catch this, because nothing was ever ungrounded.
/// </para>
/// <para>
/// <b>The control, and why it is not a prompt.</b> A model-proposed query term is accepted only
/// when every one of its tokens already appears in
/// <i>(the mapper's interest map ∪ the customer's own sentence) ∪ (the catalogue's own category
/// names and attribute/tag tokens)</i>. Terms with any token outside that set are DROPPED, and
/// each drop is recorded as a <see cref="DroppedQueryTerm"/> and printed. Prompt text telling a
/// model to ignore embedded instructions is defence in depth and is present in both prompts, but
/// it is NOT the control: a control you can talk a model out of is a request.
/// </para>
/// <para>
/// <b>Three deliberate exclusions from the vocabulary, each of them a hole if admitted.</b>
/// </para>
/// <list type="number">
///   <item>
///     <b>Product names and descriptions are NOT in it.</b> A marketplace listing's title and
///     body are seller-authored free text — admitting them would let the attacker supply the
///     vocabulary that admits their own steering terms. Only category names and the
///     attribute/tag token set are catalogue-owned enough to be a bar.
///   </item>
///   <item>
///     <b>Review bodies are NOT in it</b>, for the same reason and more directly: the review is
///     the attack channel, so it cannot also be the allow-list.
///   </item>
///   <item>
///     <b>Reviewer-inferred interests are NOT in it.</b> Only interests with
///     <see cref="InterestOrigin.Mapper"/> widen the vocabulary. Otherwise round 2's accepted
///     proposal would launder its own tokens into round 3's allow-list, and two rounds of
///     laundering is an unbounded channel wearing a bounded costume.
///   </item>
/// </list>
/// <para>
/// The customer's own in-session sentence IS admitted: they raised the topic, and a request the
/// customer typed is not an injection into their own session.
/// </para>
/// <para>
/// <b>B-9 — the control used to be monolingual, and its own corpus is not.</b> Twenty-seven of
/// the hundred and two seeded reviews are German, seven French and six Italian, and the personas
/// speak <c>de</c>, <c>fr</c> and <c>it</c>. An allow-list built only from the catalogue's
/// English strings therefore refused every LEGITIMATE non-English proposal while leaving the
/// English attack surface exactly as wide as before — the worst possible split, because the
/// visible drops were all false positives and the control looked like it was working.
/// </para>
/// <para>
/// The fix is deliberately NOT "widen the vocabulary with review text". That is the laundering
/// channel this class exists to close, and adding the attack channel to the allow-list would
/// close nothing. Instead the CATALOGUE'S OWN category and attribute vocabulary is given its
/// de/fr/it forms in <see cref="LocalisedCategoryNames"/> and
/// <see cref="LocalisedAttributeNames"/> — the same move
/// <c>SensitiveInferenceBlocklist.SpecialCategoryTerms</c> already makes for the output layer.
/// Three properties keep it honest, and <see cref="SelfCheck"/> asserts all three:
/// </para>
/// <list type="number">
///   <item>every localisation KEY must be a real category-path element or attribute key of the
///         live catalogue — the table cannot invent vocabulary the catalogue does not have, and
///         a department removed from the seed takes its translations with it;</item>
///   <item>a localised phrase naming a real catalogue leaf must be ACCEPTED — the failure B-9
///         records;</item>
///   <item>a localised phrase naming something the catalogue does NOT sell must still be
///         REFUSED — without this the widening would be indistinguishable from switching the
///         control off, which is how it would fail in the flattering direction.</item>
/// </list>
/// <para>
/// Demo-scale and hand-authored, exactly like the blocklist it mirrors: it covers the catalogue's
/// own nouns, not the languages. Saying so is cheaper than pretending it is a translation memory.
/// </para>
/// </remarks>
public sealed class QueryVocabulary
{
    private readonly HashSet<string> _tokens;
    private readonly Dictionary<string, string> _categoryPathsByKey;

    private QueryVocabulary(HashSet<string> tokens, Dictionary<string, string> categoryPathsByKey)
    {
        _tokens = tokens;
        _categoryPathsByKey = categoryPathsByKey;
    }

    /// <summary>
    /// Tokens with no steering power, admitted regardless of the catalogue.
    /// </summary>
    /// <remarks>
    /// English and German function words only. They cannot move retrieval toward a particular
    /// SKU — a query is scored on content tokens — so refusing them would only make the control
    /// reject legitimate natural-language queries and look broken, which is how a real control
    /// gets switched off. Nothing here names a product, a brand, a category or an attribute.
    /// </remarks>
    public static IReadOnlySet<string> NeutralTokens { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "and", "or", "for", "with", "without", "the", "a", "an", "in", "on", "of", "to", "at",
        "by", "from", "not", "no", "that", "this", "than", "then", "as", "is", "are", "be",
        "und", "oder", "fuer", "für", "mit", "ohne", "der", "die", "das", "den", "dem", "ein",
        "eine", "im", "am", "zum", "zur", "von", "auf", "bei", "nicht", "kein", "keine"
    };

    /// <summary>
    /// German, French and Italian forms of the catalogue's own CATEGORY names (B-9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed by the exact category-path element as the seed spells it, so
    /// <see cref="SelfCheck"/> can prove every key is a string the catalogue actually carries.
    /// A key whose category is not in the running catalogue contributes nothing.
    /// </para>
    /// <para>
    /// Forms are stored as PHRASES and tokenised by <see cref="Tokenize"/> like everything else,
    /// which is what admits a German compound ("Wanderschuhe") that no word-by-word mapping
    /// would ever produce. Accented and transliterated spellings are both listed where a model
    /// realistically writes either ("randonnée" / "randonnee"), the same discipline
    /// <c>SpecialCategoryTerms</c> applies to "für" / "fuer".
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> LocalisedCategoryNames { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // ── departments ────────────────────────────────────────────────────────────
            ["Photography"]                = ["Fotografie", "Fotografieren", "photographie", "fotografia"],
            ["Outdoor & Hiking"]           = ["Outdoor und Wandern", "Wandern", "randonnée", "randonnee", "escursionismo", "trekking"],
            ["Home Espresso"]              = ["Espresso zu Hause", "espresso maison", "espresso casa"],
            ["Gaming"]                     = ["Videospiele", "jeux vidéo", "jeux video", "videogiochi"],
            ["Kitchen & Small Appliances"] = ["Küche und Kleingeräte", "Kueche", "Kleingeraete", "cuisine et petit électroménager", "electromenager", "cucina e piccoli elettrodomestici"],
            ["Cycling"]                    = ["Velo", "Fahrrad", "Radsport", "cyclisme", "vélo", "ciclismo"],
            ["Home Audio"]                 = ["Heimaudio", "HiFi", "audio maison", "audio casa"],
            ["Power & Travel Tech"]        = ["Strom und Reisetechnik", "Reise", "énergie et voyage", "energie et voyage", "energia e viaggio"],
            ["Health & Personal Care"]     = ["Gesundheit und Körperpflege", "Koerperpflege", "santé et soins personnels", "sante", "salute e cura della persona"],

            // ── Photography ────────────────────────────────────────────────────────────
            ["Cameras"]                = ["Kameras", "Kamera", "appareils photo", "fotocamere"],
            ["Mirrorless full-frame"]  = ["spiegellose Vollformat", "Vollformat", "plein format hybride", "mirrorless full frame", "pieno formato"],
            ["Lenses"]                 = ["Objektive", "Objektiv", "objectifs", "obiettivi"],
            ["Wide-angle zoom"]        = ["Weitwinkelzoom", "Weitwinkel", "zoom grand-angle", "grandangolo"],
            ["Standard zoom"]          = ["Standardzoom", "zoom standard", "zoom standard"],
            ["Filters"]                = ["Filter", "filtres", "filtri"],
            ["Neutral density"]        = ["Graufilter", "Neutraldichte", "densité neutre", "densite neutre", "densità neutra"],
            ["Variable ND"]            = ["variabler Graufilter", "ND variable", "ND variabile"],
            ["Tripods"]                = ["Stative", "Stativ", "trépieds", "trepied", "treppiedi"],
            ["Travel tripods"]         = ["Reisestativ", "trépied de voyage", "treppiede da viaggio"],
            ["Camera support"]         = ["Kamerahalterung", "support photo", "supporto fotocamera"],
            ["Carry clips"]            = ["Trageclip", "clip de portage", "clip da trasporto"],
            ["Camera straps"]          = ["Kameragurt", "Trageriemen", "sangle photo", "tracolla fotocamera"],
            ["Camera batteries"]       = ["Kameraakku", "Akku", "batterie photo", "batteria fotocamera"],
            ["Camera backpacks"]       = ["Kamerarucksack", "sac à dos photo", "zaino fotografico"],
            ["Memory"]                 = ["Speicher", "mémoire", "memoire", "memoria"],
            ["SD cards"]               = ["SD-Karten", "Speicherkarte", "cartes SD", "schede SD"],
            ["Card readers"]           = ["Kartenleser", "lecteur de carte", "lettore di schede"],
            ["Bags"]                   = ["Taschen", "Tasche", "sacs", "borse"],

            // ── Outdoor & Hiking ───────────────────────────────────────────────────────
            ["Backpacks"]              = ["Rucksäcke", "Rucksack", "sacs à dos", "sac a dos", "zaini"],
            ["Trekking packs"]         = ["Trekkingrucksack", "Wanderrucksack", "sac à dos de randonnée", "zaino da trekking"],
            ["Chest packs"]            = ["Brusttasche", "sacoche poitrine", "marsupio pettorale"],
            ["Running vests"]          = ["Laufweste", "gilet de trail", "gilet da corsa"],
            ["Lighting"]               = ["Beleuchtung", "Licht", "éclairage", "eclairage", "illuminazione"],
            ["Headlamps"]              = ["Stirnlampe", "Stirnlampen", "lampe frontale", "lampada frontale"],
            ["Apparel"]                = ["Bekleidung", "vêtements", "vetements", "abbigliamento"],
            ["Base layers"]            = ["Baselayer", "Unterwäsche", "sous-couche", "strato base"],
            ["Shell jackets"]          = ["Hardshelljacke", "Regenjacke", "veste imperméable", "giacca impermeabile"],
            ["Trekking poles"]         = ["Trekkingstöcke", "Wanderstöcke", "Wanderstoecke", "bâtons de randonnée", "batons", "bastoncini da trekking"],
            ["Folding poles"]          = ["Faltstöcke", "bâtons pliants", "bastoncini pieghevoli"],
            ["Water treatment"]        = ["Wasseraufbereitung", "traitement de l'eau", "trattamento dell'acqua"],
            ["Squeeze filters"]        = ["Wasserfilter", "filtre à eau", "filtro per acqua"],
            ["Sleep systems"]          = ["Schlafsystem", "couchage", "sistema notte"],
            ["Sleeping mats"]          = ["Isomatte", "Schlafmatte", "matelas de sol", "materassino"],
            ["Footwear"]               = ["Schuhe", "chaussures", "calzature"],
            ["Hiking shoes"]           = ["Wanderschuhe", "Wanderschuh", "chaussures de randonnée", "chaussures de randonnee", "scarpe da trekking", "scarponcini"],
            ["Navigation"]             = ["Navigation", "navigation", "navigazione"],
            ["Satellite communicators"]= ["Satellitenkommunikator", "communicateur satellite", "comunicatore satellitare"],
            ["Trail watches"]          = ["Outdooruhr", "Trailuhr", "montre outdoor", "orologio outdoor"],

            // ── Home Espresso ──────────────────────────────────────────────────────────
            ["Machines"]               = ["Maschinen", "machines", "macchine"],
            ["Espresso machines"]      = ["Espressomaschine", "Siebträgermaschine", "machine à espresso", "macchina per espresso"],
            ["Grinders"]               = ["Mühlen", "Kaffeemühle", "Muehle", "moulins", "macinacaffè", "macinacaffe"],
            ["Electric burr grinders"] = ["elektrische Kaffeemühle", "moulin électrique", "macinacaffè elettrico"],
            ["Hand grinders"]          = ["Handmühle", "moulin manuel", "macinacaffè manuale"],
            ["Portafilters"]           = ["Siebträger", "Siebtraeger", "porte-filtre", "portafiltro"],
            ["Distribution tools"]     = ["Verteiler", "outil de distribution", "distributore"],
            ["Tampers"]                = ["Tamper", "Stopfer", "tasseur", "pressino"],
            ["Coffee"]                 = ["Kaffee", "café", "cafe", "caffè", "caffe"],
            ["Whole beans"]            = ["ganze Bohnen", "Kaffeebohnen", "grains entiers", "chicchi interi"],
            ["Maintenance"]            = ["Wartung", "Pflege", "entretien", "manutenzione"],
            ["Cleaning tablets"]       = ["Reinigungstabletten", "pastilles de nettoyage", "pastiglie detergenti"],
            ["Descaler"]               = ["Entkalker", "détartrant", "detartrant", "decalcificante"],
            ["Group brushes"]          = ["Brühkopfbürste", "brosse de groupe", "spazzola gruppo"],
            ["Accessories"]            = ["Zubehör", "Zubehoer", "accessoires", "accessori"],

            // ── Gaming ─────────────────────────────────────────────────────────────────
            ["Consoles"]               = ["Konsolen", "Konsole", "consoles", "console"],
            ["Handheld hybrid"]        = ["Handheld", "console portable", "console portatile"],
            ["Games"]                  = ["Spiele", "jeux", "giochi"],
            ["Controllers"]            = ["Controller", "manettes", "controller"],
            ["Console controllers"]    = ["Konsolencontroller", "manette de console", "controller per console"],
            ["Gaming headsets"]        = ["Gaming-Headset", "casque gaming", "cuffie gaming"],
            ["Console memory cards"]   = ["Speicherkarte", "carte mémoire", "scheda di memoria"],
            ["Docks"]                  = ["Dockingstation", "station d'accueil", "dock"],
            ["Carry cases"]            = ["Transporttasche", "étui de transport", "custodia da trasporto"],

            // ── Kitchen & Small Appliances ─────────────────────────────────────────────
            ["Blenders"]               = ["Mixer", "Standmixer", "blenders", "frullatori"],
            ["High-performance blenders"] = ["Hochleistungsmixer", "blender haute performance", "frullatore ad alte prestazioni"],
            ["Countertop blenders"]    = ["Standmixer", "blender de comptoir", "frullatore da banco"],
            ["Personal blenders"]      = ["Personal Blender", "blender individuel", "frullatore personale"],
            ["Water filtration"]       = ["Wasserfilterung", "filtration de l'eau", "filtrazione dell'acqua"],
            ["Filter cartridges"]      = ["Filterkartuschen", "Filterkartusche", "cartouches filtrantes", "cartucce filtranti"],
            ["Filter jugs"]            = ["Filterkanne", "carafe filtrante", "caraffa filtrante"],
            ["Brewing minerals"]       = ["Brühmineralien", "minéraux de brassage", "minerali per infusione"],
            ["Food storage"]           = ["Lebensmittelaufbewahrung", "conservation des aliments", "conservazione alimenti"],
            ["Vacuum canisters"]       = ["Vakuumbehälter", "boîte sous vide", "contenitore sottovuoto"],
            ["Vacuum sealers"]         = ["Vakuumierer", "machine sous vide", "macchina sottovuoto"],
            ["Kitchen scales"]         = ["Küchenwaage", "Kuechenwaage", "balance de cuisine", "bilancia da cucina"],
            ["Precision scales"]       = ["Feinwaage", "balance de précision", "bilancia di precisione"],
            ["Cookware"]               = ["Kochgeschirr", "ustensiles de cuisson", "pentole"],
            ["Pressure cookers"]       = ["Schnellkochtopf", "autocuiseur", "pentola a pressione"],
            ["Food steamers"]          = ["Dampfgarer", "cuiseur vapeur", "vaporiera"],
            ["Coffee & tea"]           = ["Kaffee und Tee", "café et thé", "caffè e tè"],
            ["Milk pitchers"]          = ["Milchkännchen", "pot à lait", "lattiera"],
            ["Milk frothers"]          = ["Milchaufschäumer", "mousseur à lait", "montalatte"],
            ["Milk thermometers"]      = ["Milchthermometer", "thermomètre à lait", "termometro per latte"],
            ["Espresso cups"]          = ["Espressotassen", "tasses à espresso", "tazzine da caffè"],

            // ── Cycling ────────────────────────────────────────────────────────────────
            ["Handlebar bags"]         = ["Lenkertasche", "sacoche de guidon", "borsa da manubrio"],
            ["Frame bags"]             = ["Rahmentasche", "sacoche de cadre", "borsa da telaio"],
            ["Training"]               = ["Training", "entraînement", "entrainement", "allenamento"],
            ["Heart-rate monitors"]    = ["Herzfrequenzmesser", "Pulsgurt", "cardiofréquencemètre", "cardiofrequenzimetro"],
            ["Smart trainers"]         = ["Rollentrainer", "home-trainer connecté", "rullo smart"],
            ["Computers"]              = ["Fahrradcomputer", "compteurs", "ciclocomputer"],
            ["GPS bike computers"]     = ["GPS-Fahrradcomputer", "compteur GPS", "ciclocomputer GPS"],
            ["Front lights"]           = ["Frontlicht", "éclairage avant", "luce anteriore"],
            ["Rear lights"]            = ["Rücklicht", "Ruecklicht", "éclairage arrière", "luce posteriore"],
            ["Tyres"]                  = ["Reifen", "pneus", "pneumatici"],
            ["Road tyres"]             = ["Rennradreifen", "pneus route", "pneumatici da strada"],
            ["Helmets"]                = ["Helme", "Helm", "casques", "caschi"],
            ["Road helmets"]           = ["Rennradhelm", "casque route", "casco da strada"],
            ["Tools"]                  = ["Werkzeug", "outils", "attrezzi"],
            ["Multi-tools"]            = ["Multitool", "Multiwerkzeug", "outil multifonction", "multiutensile"],
            ["Overshoes"]              = ["Überschuhe", "Ueberschuhe", "couvre-chaussures", "copriscarpe"],
            ["Mudguards"]              = ["Schutzbleche", "garde-boue", "parafanghi"],
            ["Full-length mudguards"]  = ["Vollschutzblech", "garde-boue intégral", "parafango integrale"],

            // ── Home Audio ─────────────────────────────────────────────────────────────
            ["Headphones"]             = ["Kopfhörer", "Kopfhoerer", "casques audio", "cuffie"],
            ["Over-ear wireless"]      = ["kabellose Over-Ear", "circum-aural sans fil", "over-ear senza fili"],
            ["Over-ear wired"]         = ["kabelgebundene Over-Ear", "circum-aural filaire", "over-ear con cavo"],
            ["In-ear monitors"]        = ["In-Ear-Hörer", "intra-auriculaires", "auricolari"],
            ["DACs"]                   = ["DAC", "Wandler", "convertisseur", "convertitore"],
            ["Desktop DACs"]           = ["Desktop-DAC", "DAC de bureau", "DAC da scrivania"],
            ["Portable DACs"]          = ["tragbarer DAC", "DAC portable", "DAC portatile"],
            ["Speakers"]               = ["Lautsprecher", "enceintes", "diffusori", "altoparlanti"],
            ["Active bookshelf"]       = ["Aktivlautsprecher", "enceinte active", "diffusore attivo"],
            ["Smart speakers"]         = ["Smart Speaker", "enceinte connectée", "altoparlante smart"],
            ["Speaker stands"]         = ["Lautsprecherständer", "pieds d'enceinte", "supporti per diffusori"],
            ["Streamers"]              = ["Streamer", "lecteur réseau", "lettore di rete"],
            ["Network streamers"]      = ["Netzwerkplayer", "lecteur réseau", "streamer di rete"],
            ["Cables"]                 = ["Kabel", "câbles", "cables", "cavi"],
            ["Analogue interconnects"] = ["Analogkabel", "câble analogique", "cavo analogico"],
            ["Linear power supplies"]  = ["Linearnetzteil", "alimentation linéaire", "alimentatore lineare"],

            // ── Power & Travel Tech ────────────────────────────────────────────────────
            ["Power banks"]            = ["Powerbank", "Zusatzakku", "batterie externe", "batteria esterna"],
            ["High-output power banks"]= ["Hochleistungs-Powerbank", "batterie externe haute puissance", "power bank ad alta potenza"],
            ["Ultralight power banks"] = ["ultraleichte Powerbank", "batterie externe ultralégère", "power bank ultraleggera"],
            ["USB-C cables"]           = ["USB-C-Kabel", "câble USB-C", "cavo USB-C"],
            ["Protection"]             = ["Schutz", "protection", "protezione"],
            ["Dry bags"]               = ["Packsack", "Trockensack", "sac étanche", "sacca stagna"],
            ["Chargers"]               = ["Ladegeräte", "Ladegeraet", "chargeurs", "caricabatterie"],
            ["GaN wall chargers"]      = ["GaN-Netzteil", "chargeur secteur GaN", "caricatore GaN"],
            ["Solar chargers"]         = ["Solarladegerät", "chargeur solaire", "caricatore solare"],
            ["Adapters"]               = ["Adapter", "adaptateurs", "adattatori"],
            ["Travel adapters"]        = ["Reiseadapter", "adaptateur de voyage", "adattatore da viaggio"],
            ["Power"]                  = ["Strom", "Energie", "alimentation", "alimentazione"],
            ["Storage"]                = ["Speicher", "stockage", "archiviazione"],

            // ── Health & Personal Care ─────────────────────────────────────────────────
            ["Blood pressure"]         = ["Blutdruck", "tension artérielle", "pressione sanguigna"],
            ["Upper-arm monitors"]     = ["Oberarmmessgerät", "tensiomètre au bras", "misuratore da braccio"],
            ["Cuffs"]                  = ["Manschetten", "Manschette", "brassards", "bracciali"],
            ["Medication management"]  = ["Medikamentenverwaltung", "gestion des médicaments", "gestione dei farmaci"],
            ["Pill organisers"]        = ["Pillendose", "pilulier", "portapillole"],
            ["Home diagnostics"]       = ["Heimdiagnostik", "diagnostic à domicile", "diagnostica domiciliare"],
            ["Pulse oximeters"]        = ["Pulsoximeter", "oxymètre de pouls", "pulsossimetro"],

            // ── shared leaf nouns ──────────────────────────────────────────────────────
            ["Audio"]                  = ["Audio", "audio", "audio"],
            ["Adventure"]              = ["Abenteuer", "aventure", "avventura"],
            ["Racing"]                 = ["Rennspiel", "course", "corsa"],
            ["Party"]                  = ["Partyspiel", "jeu de fête", "gioco di gruppo"],
        };

    /// <summary>
    /// German, French and Italian forms of the catalogue's own ATTRIBUTE keys (B-9).
    /// </summary>
    /// <remarks>
    /// Keyed by the exact attribute key as the category seed spells it, so
    /// <see cref="SelfCheck"/> can prove every key is one the catalogue actually declares. Values
    /// are the ones a customer or a reviewer writing in de/fr/it actually types — an attribute
    /// name nobody writes buys nothing and only widens the surface.
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> LocalisedAttributeNames { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Weight"]           = ["Gewicht", "poids", "peso"],
            ["Material"]         = ["Material", "matière", "matiere", "materiale"],
            ["Capacity"]         = ["Kapazität", "Kapazitaet", "Fassungsvermögen", "capacité", "capacite", "capacità", "capacita"],
            ["Battery life"]     = ["Akkulaufzeit", "autonomie", "autonomia"],
            ["Battery"]          = ["Akku", "Batterie", "batterie", "batteria"],
            ["Size"]             = ["Grösse", "Groesse", "Größe", "taille", "taglia"],
            ["Sizes"]            = ["Grössen", "tailles", "taglie"],
            ["Length"]           = ["Länge", "Laenge", "longueur", "lunghezza"],
            ["Height"]           = ["Höhe", "Hoehe", "hauteur", "altezza"],
            ["Thickness"]        = ["Dicke", "épaisseur", "epaisseur", "spessore"],
            ["Density"]          = ["Dichte", "densité", "densite", "densità"],
            ["Coating"]          = ["Vergütung", "Beschichtung", "traitement", "trattamento"],
            ["Filter thread"]    = ["Filtergewinde", "filetage de filtre", "filettatura filtro"],
            ["Focal length"]     = ["Brennweite", "focale", "focale"],
            ["Maximum aperture"] = ["Lichtstärke", "Blende", "ouverture maximale", "apertura massima"],
            ["Lens mount"]       = ["Bajonett", "monture", "innesto"],
            ["Sensor"]           = ["Sensor", "capteur", "sensore"],
            ["Resolution"]       = ["Auflösung", "Aufloesung", "résolution", "risoluzione"],
            ["Weather sealing"]  = ["Wetterschutz", "Abdichtung", "tropicalisation", "tenuta alle intemperie"],
            ["Weather protection"] = ["Wetterschutz", "protection météo", "protezione dalle intemperie"],
            ["Water resistance"] = ["Wasserdichtigkeit", "résistance à l'eau", "resistenza all'acqua"],
            ["Connection"]       = ["Verbindung", "Anschluss", "connexion", "connessione"],
            ["Connectivity"]     = ["Konnektivität", "connectivité", "connettività"],
            ["Ports"]            = ["Anschlüsse", "Anschluesse", "ports", "porte"],
            ["Output"]           = ["Ausgang", "Leistung", "sortie", "uscita"],
            ["Max output"]       = ["maximale Leistung", "sortie maximale", "uscita massima"],
            ["Power"]            = ["Leistung", "puissance", "potenza"],
            ["Pump pressure"]    = ["Pumpendruck", "pression de pompe", "pressione della pompa"],
            ["Burr type"]        = ["Mahlwerk", "type de meules", "tipo di macine"],
            ["Burr size"]        = ["Mahlwerksgrösse", "taille des meules", "dimensione macine"],
            ["Grind settings"]   = ["Mahlgradstufen", "réglages de mouture", "regolazioni di macinatura"],
            ["Roast"]            = ["Röstung", "Roestung", "torréfaction", "tostatura"],
            ["Origin"]           = ["Herkunft", "origine", "origine"],
            ["Fit"]              = ["Passform", "coupe", "vestibilità"],
            ["Upper"]            = ["Obermaterial", "tige", "tomaia"],
            ["Sole"]             = ["Sohle", "semelle", "suola"],
            ["Fabric weight"]    = ["Flächengewicht", "grammage", "grammatura"],
            ["Composition"]      = ["Zusammensetzung", "composition", "composizione"],
            ["Fill"]             = ["Füllung", "Fuellung", "garnissage", "imbottitura"],
            ["Packed length"]    = ["Packmass", "Packmaß", "longueur pliée", "lunghezza da chiuso"],
            ["Packed size"]      = ["Packmass", "taille pliée", "dimensioni da chiuso"],
            ["Folded length"]    = ["Faltlänge", "longueur pliée", "lunghezza piegata"],
            ["Load capacity"]    = ["Traglast", "charge maximale", "portata"],
            ["Flow rate"]        = ["Durchflussrate", "débit", "portata d'acqua"],
            ["Cartridge life"]   = ["Kartuschenlebensdauer", "durée de la cartouche", "durata della cartuccia"],
            ["Impedance"]        = ["Impedanz", "impédance", "impedenza"],
            ["Driver"]           = ["Treiber", "transducteur", "trasduttore"],
            ["Noise cancelling"] = ["Geräuschunterdrückung", "réduction de bruit", "cancellazione del rumore"],
            ["Sample rate"]      = ["Abtastrate", "fréquence d'échantillonnage", "frequenza di campionamento"],
            ["Standard"]         = ["Norm", "norme", "norma"],
            ["Vents"]            = ["Belüftungsöffnungen", "aérations", "prese d'aria"],
            ["Retention"]        = ["Verstellsystem", "système de serrage", "sistema di regolazione"],
            ["Closure"]          = ["Verschluss", "fermeture", "chiusura"],
            ["Compatibility"]    = ["Kompatibilität", "compatibilité", "compatibilità"],
            ["Recharge time"]    = ["Ladezeit", "temps de recharge", "tempo di ricarica"],
            ["Burn time"]        = ["Leuchtdauer", "autonomie d'éclairage", "autonomia di illuminazione"],
            ["Range"]            = ["Reichweite", "portée", "portata"],
            ["Accuracy"]         = ["Genauigkeit", "précision", "precisione"],
            ["Cuff size"]        = ["Manschettengrösse", "taille du brassard", "misura del bracciale"],
            ["Type"]             = ["Typ", "type", "tipo"],
            ["Functions"]        = ["Funktionen", "fonctions", "funzioni"],
            ["Timer"]            = ["Timer", "minuterie", "timer"],
            ["Display"]          = ["Anzeige", "écran", "display"],
            ["Storage"]          = ["Speicher", "stockage", "memoria"],
        };

    /// <summary>Every accepted token, ordinal. Exposed so the console can print its size.</summary>
    public IReadOnlySet<string> Tokens => _tokens;

    /// <summary>How many category paths this vocabulary can resolve a <c>next_category</c> against.</summary>
    public int CategoryCount => _categoryPathsByKey.Count;

    /// <summary>
    /// Builds the allowed vocabulary for one point in a run.
    /// </summary>
    /// <remarks>
    /// Rebuild it whenever the MAPPER-origin interest set changes. It is cheap — a few thousand
    /// short strings over a 76-product catalogue — and rebuilding is the honest thing to do,
    /// because a stale vocabulary is a vocabulary somebody widened without saying so.
    /// </remarks>
    /// <param name="catalogue">The catalogue. Supplies category names and attribute/tag tokens.</param>
    /// <param name="interests">The running interest map. Only <see cref="InterestOrigin.Mapper"/> entries contribute.</param>
    /// <param name="sessionRequest">What the customer typed this session, if anything.</param>
    public static QueryVocabulary Build(
        Catalogue catalogue,
        IEnumerable<Interest>? interests,
        string? sessionRequest = null)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        // B-9's gate. Runs once per process, on the first vocabulary any path builds — the demo,
        // the loop, every eval, every dry run — so a broken localisation table stops the run
        // instead of quietly narrowing or widening the control. Re-entrant by design: SelfCheck
        // builds a vocabulary of its own.
        EnsureSelfChecked(catalogue);

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ── (a) the catalogue's own CATEGORY names, every node of the tree ───────────
        foreach (var category in catalogue.Categories)
        {
            categories[PathKey(category.Path)] = string.Join(" > ", category.Path);
            categories[category.Id] = string.Join(" > ", category.Path);
            categories[category.LeafName] = string.Join(" > ", category.Path);

            foreach (var element in category.Path)
            {
                AddAll(tokens, element);
                AddLocalisedForms(tokens, LocalisedCategoryNames, element);
            }

            // (a2) B-9 — the de/fr/it forms of the ATTRIBUTE keys this category declares.
            foreach (var attribute in category.AttributeSchema)
                AddLocalisedForms(tokens, LocalisedAttributeNames, attribute);
        }

        // ── (b) the catalogue's own ATTRIBUTE and TAG tokens ─────────────────────────
        //     Product.Attributes already fuses tags (whole and suffix), spec keys, spec values
        //     and key=value pairs through one normaliser, so this is exactly the token set an
        //     `attr:` citation may legitimately resolve against — nothing wider.
        foreach (var product in catalogue.All)
        {
            foreach (var attribute in catalogue.AttributesOf(product)) AddAll(tokens, attribute);
            foreach (var element in product.CategoryPath)
            {
                AddAll(tokens, element);
                AddLocalisedForms(tokens, LocalisedCategoryNames, element);
            }

            // (b2) B-9 — the de/fr/it forms of the spec keys this product actually carries.
            foreach (var (key, _) in product.Specs)
                AddLocalisedForms(tokens, LocalisedAttributeNames, key);
        }

        // ── (c) the MAPPER's interest map ────────────────────────────────────────────
        if (interests is not null)
        {
            foreach (var interest in interests)
            {
                // Reviewer-inferred interests do NOT widen the vocabulary — see the type remarks.
                if (interest.Origin != InterestOrigin.Mapper) continue;

                AddAll(tokens, interest.Label);
                foreach (var term in interest.QueryTerms) AddAll(tokens, term);
                foreach (var hint in interest.CategoryHints) AddAll(tokens, hint);
                foreach (var (key, value) in interest.AttributeHints)
                {
                    AddAll(tokens, key);
                    AddAll(tokens, value);
                }
            }
        }

        // ── (d) the customer's own sentence ──────────────────────────────────────────
        AddAll(tokens, sessionRequest);

        return new QueryVocabulary(tokens, categories);
    }

    /// <summary>
    /// True when every token of <paramref name="term"/> is in the vocabulary.
    /// </summary>
    /// <param name="term">A model-proposed query phrase.</param>
    /// <param name="offendingTokens">The tokens that are not, ordered and de-duplicated.</param>
    public bool Accepts(string? term, out IReadOnlyList<string> offendingTokens)
    {
        var offending = new List<string>();
        offendingTokens = offending;

        if (string.IsNullOrWhiteSpace(term)) return false;

        var seenOffender = new HashSet<string>(StringComparer.Ordinal);
        bool sawContent = false;

        foreach (var token in Tokenize(term))
        {
            sawContent = true;
            if (_tokens.Contains(token) || NeutralTokens.Contains(token)) continue;
            if (seenOffender.Add(token)) offending.Add(token);
        }

        // A phrase that tokenises to nothing is not "clean", it is empty — and an empty query
        // retrieves the whole catalogue. Refuse it.
        if (!sawContent)
        {
            offending.Add("(no usable token)");
            return false;
        }

        return offending.Count == 0;
    }

    /// <summary>
    /// Filters a proposed term list, recording every refusal.
    /// </summary>
    /// <param name="terms">The model's proposed query terms.</param>
    /// <param name="proposedFor">What they were proposed for — printed in the drop line.</param>
    /// <param name="drops">The run's drop ledger; every refusal is appended.</param>
    /// <returns>The surviving terms, in input order, de-duplicated ordinally.</returns>
    public IReadOnlyList<string> Filter(
        IEnumerable<string>? terms,
        string proposedFor,
        ICollection<DroppedQueryTerm> drops)
    {
        ArgumentNullException.ThrowIfNull(drops);

        var kept = new List<string>();
        if (terms is null) return kept;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in terms)
        {
            var term = raw?.Trim() ?? string.Empty;
            if (term.Length == 0) continue;

            if (!Accepts(term, out var offending))
            {
                drops.Add(new DroppedQueryTerm(term, proposedFor, offending));
                continue;
            }

            if (seen.Add(term)) kept.Add(term);
        }

        return kept;
    }

    /// <summary>
    /// Filters one query string. Returns null when the query does not survive, which makes the
    /// gap that carried it unrunnable — and an unrunnable gap is dropped rather than searched.
    /// </summary>
    /// <param name="query">The model's <c>next_query</c>.</param>
    /// <param name="proposedFor">What it was proposed for.</param>
    /// <param name="drops">The run's drop ledger.</param>
    public string? FilterQuery(string? query, string proposedFor, ICollection<DroppedQueryTerm> drops)
    {
        ArgumentNullException.ThrowIfNull(drops);

        var text = query?.Trim() ?? string.Empty;
        if (text.Length == 0) return null;

        if (Accepts(text, out var offending)) return text;

        drops.Add(new DroppedQueryTerm(text, proposedFor, offending));
        return null;
    }

    /// <summary>
    /// Resolves a model-proposed category to a real catalogue path, or null.
    /// </summary>
    /// <remarks>
    /// A category is a HARD pre-filter on retrieval, so an unresolvable one must not be passed
    /// through as free text: doing so would either silently widen the search or silently empty
    /// it, and both look like the model's judgement rather than a wiring fault.
    /// </remarks>
    /// <param name="category">A category id, a leaf name, or a <c>" &gt; "</c>-joined path.</param>
    public string? ResolveCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return null;

        var needle = category.Trim();
        if (_categoryPathsByKey.TryGetValue(needle, out var direct)) return direct;

        var normalised = PathKey(needle.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return _categoryPathsByKey.TryGetValue(normalised, out var byPath) ? byPath : null;
    }

    /// <summary>
    /// Filters proposed attribute name/value pairs to ones the catalogue actually carries.
    /// </summary>
    /// <param name="attributes">The model's <c>next_attributes</c>.</param>
    /// <param name="proposedFor">What they were proposed for.</param>
    /// <param name="drops">The run's drop ledger.</param>
    public IReadOnlyDictionary<string, string> FilterAttributes(
        IReadOnlyDictionary<string, string>? attributes,
        string proposedFor,
        ICollection<DroppedQueryTerm> drops)
    {
        ArgumentNullException.ThrowIfNull(drops);

        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        if (attributes is null) return kept;

        foreach (var (key, value) in attributes)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;

            if (!Accepts(key, out var badKey))
            {
                drops.Add(new DroppedQueryTerm($"{key}={value}", proposedFor, badKey));
                continue;
            }

            if (!Accepts(value, out var badValue))
            {
                drops.Add(new DroppedQueryTerm($"{key}={value}", proposedFor, badValue));
                continue;
            }

            kept[key.Trim()] = value.Trim();
        }

        return kept;
    }

    /// <summary>
    /// THE tokeniser both sides of the check run through, so the vocabulary and the candidate
    /// term can never disagree on casing, hyphenation or punctuation.
    /// </summary>
    /// <remarks>
    /// Lower-invariant; every non-alphanumeric character is a separator; tokens of a single
    /// character are ignored on BOTH sides (they carry no steering meaning and admitting them
    /// would neither open nor close anything). Digits are kept and are checked like any other
    /// token — a bare article number is exactly the sort of thing an injected query would carry.
    /// </remarks>
    /// <param name="text">Any label, query, attribute key or catalogue string.</param>
    public static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (builder.Length > 1) yield return builder.ToString();
            builder.Clear();
        }

        if (builder.Length > 1) yield return builder.ToString();
    }

    /// <summary>
    /// B-9's two-sided gate on the localisation tables, run against a LIVE catalogue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is deliberately not a coverage report. It asserts the three properties that make the
    /// widening a fix rather than a hole, and it can go red in both directions:
    /// </para>
    /// <list type="number">
    ///   <item><b>KEYS ARE CATALOGUE-OWNED.</b> Every key of both tables must be a category-path
    ///         element, an attribute-schema entry or a product spec key of THIS catalogue. Invent
    ///         a key and this fails — which is what stops the table becoming a second, private
    ///         vocabulary nobody diffed against the seed.</item>
    ///   <item><b>ACCEPTED.</b> Each pinned localised phrase — the de/fr/it name of a real
    ///         catalogue leaf — must pass <see cref="Accepts"/>. These are the proposals B-9
    ///         records as wrongly refused, including the design's own Italian "Hiking shoes".</item>
    ///   <item><b>STILL REFUSED.</b> Each pinned foreign phrase naming something the catalogue
    ///         does NOT sell must still fail <see cref="Accepts"/>. Without this arm, deleting
    ///         the control entirely would pass arm 2 — the flattering direction.</item>
    /// </list>
    /// <para>
    /// ⚠ The refusal arm carries the discrimination. Read arm 2 alone and a vocabulary that
    /// admits every word in three languages looks like a fix.
    /// </para>
    /// </remarks>
    /// <param name="catalogue">The catalogue to check the tables against.</param>
    /// <returns>One line per violation, empty when all three properties hold.</returns>
    public static IReadOnlyList<string> SelfCheck(Catalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var failures = new List<string>();

        // ── 1. every key is a string the catalogue itself carries ────────────────────
        var categoryStrings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var attributeStrings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var category in catalogue.Categories)
        {
            foreach (var element in category.Path) categoryStrings.Add(element);
            foreach (var attribute in category.AttributeSchema) attributeStrings.Add(attribute);
        }

        foreach (var product in catalogue.All)
        {
            foreach (var element in product.CategoryPath) categoryStrings.Add(element);
            foreach (var (key, _) in product.Specs) attributeStrings.Add(key);
        }

        foreach (var key in LocalisedCategoryNames.Keys)
            if (!categoryStrings.Contains(key))
                failures.Add($"LocalisedCategoryNames key '{key}' is not a category-path element of this catalogue.");

        foreach (var key in LocalisedAttributeNames.Keys)
            if (!attributeStrings.Contains(key))
                failures.Add($"LocalisedAttributeNames key '{key}' is not an attribute key of this catalogue.");

        // ── 2 and 3. the vocabulary a full run would build, then both arms ───────────
        var vocabulary = Build(catalogue, interests: null, sessionRequest: null);

        foreach (var phrase in LocalisedPhrasesThatMustBeAccepted)
            if (!vocabulary.Accepts(phrase, out var offending))
                failures.Add($"MUST ACCEPT '{phrase}' — refused on [{string.Join(", ", offending)}].");

        foreach (var phrase in ForeignPhrasesThatMustStillBeRefused)
            if (vocabulary.Accepts(phrase, out _))
                failures.Add($"MUST REFUSE '{phrase}' — the widening admitted it. The control is now a request.");

        return failures;
    }

    /// <summary>
    /// Localised names of REAL catalogue leaves. Every one must be accepted (<see cref="SelfCheck"/>
    /// arm 2). The Italian hiking-shoe phrase is the design's own B-9 witness — Renzo's
    /// verified-purchase review of GLX-2008 is written in Italian.
    /// </summary>
    public static IReadOnlyList<string> LocalisedPhrasesThatMustBeAccepted { get; } =
    [
        "scarpe da trekking",            // it — "Hiking shoes"
        "Wanderschuhe",                  // de — "Hiking shoes", one compound token
        "chaussures de randonnee",       // fr — "Hiking shoes"
        "Stirnlampe",                    // de — "Headlamps"
        "Trekkingrucksack",              // de — "Trekking packs"
        "lampada frontale",              // it — "Headlamps"
        "trepied de voyage",             // fr — "Travel tripods"
        "Graufilter",                    // de — "Neutral density"
        "macinacaffe manuale",           // it — "Hand grinders"
        "batterie externe",              // fr — "Power banks"
        "Gewicht",                       // de — the "Weight" attribute key
        "suola",                         // it — the "Sole" attribute key
    ];

    /// <summary>
    /// Foreign-language phrases naming things this catalogue does NOT sell. Every one must still
    /// be refused (<see cref="SelfCheck"/> arm 3) — the arm that separates a widened vocabulary
    /// from a switched-off one.
    /// </summary>
    public static IReadOnlyList<string> ForeignPhrasesThatMustStillBeRefused { get; } =
    [
        "Waschmaschine",                 // de — washing machine
        "montre de luxe",                // fr — luxury watch
        "integratori dimagranti",        // it — slimming supplements
        "Zigaretten",                    // de — cigarettes
        "assurance voyage",              // fr — travel insurance
        "criptovaluta",                  // it — cryptocurrency
    ];

    private static readonly object SelfCheckGate = new();
    private static bool _selfCheckRunning;
    private static IReadOnlyList<string>? _selfCheckResult;

    private static void EnsureSelfChecked(Catalogue catalogue)
    {
        IReadOnlyList<string> result;

        lock (SelfCheckGate)
        {
            // The re-entrant Build inside SelfCheck. Checking it here rather than passing a flag
            // keeps Build's public signature free of a testing concern.
            if (_selfCheckRunning) return;

            if (_selfCheckResult is null)
            {
                _selfCheckRunning = true;
                try { _selfCheckResult = SelfCheck(catalogue); }
                finally { _selfCheckRunning = false; }
            }

            result = _selfCheckResult;
        }

        if (result.Count == 0) return;

        throw new InvalidOperationException(
            "QueryVocabulary's B-9 localisation tables failed their own gate, so the D-3 control " +
            "is not in a state anyone should quote a containment number from:" +
            Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", result));
    }

    private static void AddAll(HashSet<string> tokens, string? text)
    {
        foreach (var token in Tokenize(text)) tokens.Add(token);
    }

    private static void AddLocalisedForms(
        HashSet<string> tokens,
        IReadOnlyDictionary<string, IReadOnlyList<string>> table,
        string? catalogueString)
    {
        if (string.IsNullOrWhiteSpace(catalogueString)) return;
        if (!table.TryGetValue(catalogueString, out var forms)) return;
        foreach (var form in forms) AddAll(tokens, form);
    }

    private static string PathKey(IReadOnlyList<string> path) => string.Join(" > ", path);
}
