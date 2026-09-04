// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Catalog;

/// <summary>
/// The hardcoded category tree (design §B.1): nine root departments, their groups, and
/// every leaf a seeded product sits in. Each leaf carries the attribute schema every
/// product in it MUST fill, and the <see cref="Category.SensitiveInference"/> flag that
/// governs unsolicited-inference suppression (§F.5).
/// </summary>
/// <remarks>
/// <para>
/// ⚠ NAMESPACE — this folder is <c>Catalogue/</c> (design §G) but the namespace is
/// <c>Galaxus.RecommendationAgent.Catalog</c>, deliberately one letter short. A namespace
/// named <c>…RecommendationAgent.Catalogue</c> containing a type named <c>Catalogue</c>
/// compiles, but every sibling namespace under <c>Galaxus.RecommendationAgent</c>
/// (<c>.Tools</c>, <c>.Guardrails</c>, <c>.Retrieval</c>, <c>.Signals</c>, <c>.Demos</c>)
/// then binds the bare word <c>Catalogue</c> to the NAMESPACE and fails with CS0234 on
/// <c>Catalogue.Default</c>. That was verified against the real compiler, not assumed.
/// The eval design's own code sketch already writes
/// <c>using Galaxus.RecommendationAgent.Catalog;</c>, so this is the spelling both lanes
/// were going to use anyway.
/// </para>
/// <para>
/// <b>Eight roots plus one.</b> §B.1 specifies eight departments holding exactly 72
/// products. The ninth root — <c>Health &amp; Personal Care</c>, four products — is the
/// §0.5 / D-6 plant, and it is DECLARED rather than smuggled: without at least one leaf
/// where <see cref="Category.SensitiveInference"/> is true, the sensitive-suppression
/// eval pair has a chance floor of 1.0. It would read as a clean pass while testing
/// nothing, which is exactly the shape §0.5 condemns for <c>PlaceOrder</c>. Consumers
/// that need the §B.1 headline count read
/// <see cref="Catalogue.CoreProductCount"/> (72); consumers that need every sellable
/// item read <see cref="Catalogue.All"/> (76).
/// </para>
/// <para>
/// <b>Sensitivity is inherited.</b> A node marked sensitive makes every descendant
/// sensitive; <see cref="Catalogue.SensitiveCategories"/> resolves that inheritance and
/// publishes the resulting LEAF NAMES, because the eval's D3 check is
/// <c>presented.LeafCategory ∈ ForbiddenCategories</c>.
/// </para>
/// </remarks>
public static class CategorySeed
{
    private static readonly string[] None = [];

    /// <summary>Every node of the tree, roots first, then groups, then leaves.</summary>
    public static IReadOnlyList<Category> All { get; } =
    [
        // ══ 1. Photography ══════════════════════════════════════════════════════════
        new("CAT-PHO",                ["Photography"],                                                    null,             None, false),
        new("CAT-PHO-CAM",            ["Photography", "Cameras"],                                         "CAT-PHO",        None, false),
        new("CAT-PHO-CAM-FF",         ["Photography", "Cameras", "Mirrorless full-frame"],                "CAT-PHO-CAM",    ["Sensor", "Resolution", "Lens mount", "Weather sealing", "Weight"], false),
        new("CAT-PHO-LEN",            ["Photography", "Lenses"],                                          "CAT-PHO",        None, false),
        new("CAT-PHO-LEN-WIDE",       ["Photography", "Lenses", "Wide-angle zoom"],                       "CAT-PHO-LEN",    ["Lens mount", "Focal length", "Maximum aperture", "Filter thread", "Weather sealing", "Weight"], false),
        new("CAT-PHO-LEN-STD",        ["Photography", "Lenses", "Standard zoom"],                         "CAT-PHO-LEN",    ["Lens mount", "Focal length", "Maximum aperture", "Filter thread", "Weather sealing", "Weight"], false),
        new("CAT-PHO-FIL",            ["Photography", "Filters"],                                         "CAT-PHO",        None, false),
        new("CAT-PHO-FIL-ND",         ["Photography", "Filters", "Neutral density"],                      "CAT-PHO-FIL",    ["Filter thread", "Density", "Coating", "Material"], false),
        new("CAT-PHO-FIL-VND",        ["Photography", "Filters", "Variable ND"],                          "CAT-PHO-FIL",    ["Filter thread", "Density", "Coating", "Material"], false),
        new("CAT-PHO-TRI",            ["Photography", "Tripods"],                                         "CAT-PHO",        None, false),
        new("CAT-PHO-TRI-TRAVEL",     ["Photography", "Tripods", "Travel tripods"],                       "CAT-PHO-TRI",    ["Material", "Folded length", "Maximum height", "Load capacity", "Weight"], false),
        new("CAT-PHO-SUP",            ["Photography", "Camera support"],                                  "CAT-PHO",        None, false),
        new("CAT-PHO-SUP-CLIP",       ["Photography", "Camera support", "Carry clips"],                   "CAT-PHO-SUP",    ["Mount standard", "Material", "Load capacity", "Weight"], false),
        new("CAT-PHO-SUP-STRAP",      ["Photography", "Camera support", "Camera straps"],                 "CAT-PHO-SUP",    ["Attachment", "Length range", "Material", "Weight"], false),
        new("CAT-PHO-PWR",            ["Photography", "Power"],                                           "CAT-PHO",        None, false),
        new("CAT-PHO-PWR-BATT",       ["Photography", "Power", "Camera batteries"],                       "CAT-PHO-PWR",    ["Battery type", "Capacity", "Pack size", "Weight"], false),
        new("CAT-PHO-BAG",            ["Photography", "Bags"],                                            "CAT-PHO",        None, false),
        new("CAT-PHO-BAG-PACK",       ["Photography", "Bags", "Camera backpacks"],                        "CAT-PHO-BAG",    ["Capacity", "Laptop compartment", "Weather protection", "Weight"], false),
        new("CAT-PHO-MEM",            ["Photography", "Memory"],                                          "CAT-PHO",        None, false),
        new("CAT-PHO-MEM-SD",         ["Photography", "Memory", "SD cards"],                              "CAT-PHO-MEM",    ["Card format", "Capacity", "Read speed", "Write speed"], false),

        // ══ 2. Outdoor & Hiking ═════════════════════════════════════════════════════
        new("CAT-OUT",                ["Outdoor & Hiking"],                                               null,             None, false),
        new("CAT-OUT-BAG",            ["Outdoor & Hiking", "Backpacks"],                                  "CAT-OUT",        None, false),
        new("CAT-OUT-BAG-TREK",       ["Outdoor & Hiking", "Backpacks", "Trekking packs"],                "CAT-OUT-BAG",    ["Capacity", "Back system", "Rain cover", "Weight"], false),
        new("CAT-OUT-BAG-CHEST",      ["Outdoor & Hiking", "Backpacks", "Chest packs"],                   "CAT-OUT-BAG",    ["Capacity", "Mounting", "Weather protection", "Weight"], false),
        new("CAT-OUT-LGT",            ["Outdoor & Hiking", "Lighting"],                                   "CAT-OUT",        None, false),
        new("CAT-OUT-LGT-HEAD",       ["Outdoor & Hiking", "Lighting", "Headlamps"],                      "CAT-OUT-LGT",    ["Max output", "Battery", "Burn time", "Weight"], false),
        new("CAT-OUT-APP",            ["Outdoor & Hiking", "Apparel"],                                    "CAT-OUT",        None, false),
        new("CAT-OUT-APP-BASE",       ["Outdoor & Hiking", "Apparel", "Base layers"],                     "CAT-OUT-APP",    ["Material", "Fabric weight", "Fit", "Weight"], false),
        new("CAT-OUT-APP-SHELL",      ["Outdoor & Hiking", "Apparel", "Shell jackets"],                   "CAT-OUT-APP",    ["Membrane", "Water resistance", "Hood", "Weight"], false),
        new("CAT-OUT-POL",            ["Outdoor & Hiking", "Trekking poles"],                             "CAT-OUT",        None, false),
        new("CAT-OUT-POL-FOLD",       ["Outdoor & Hiking", "Trekking poles", "Folding poles"],            "CAT-OUT-POL",    ["Material", "Packed length", "Adjustment", "Weight"], false),
        new("CAT-OUT-WAT",            ["Outdoor & Hiking", "Water treatment"],                            "CAT-OUT",        None, false),
        new("CAT-OUT-WAT-SQZ",        ["Outdoor & Hiking", "Water treatment", "Squeeze filters"],         "CAT-OUT-WAT",    ["Filter type", "Filter capacity", "Flow rate", "Weight"], false),
        new("CAT-OUT-SLP",            ["Outdoor & Hiking", "Sleep systems"],                              "CAT-OUT",        None, false),
        new("CAT-OUT-SLP-MAT",        ["Outdoor & Hiking", "Sleep systems", "Sleeping mats"],             "CAT-OUT-SLP",    ["R-value", "Packed size", "Thickness", "Weight"], false),
        new("CAT-OUT-FTW",            ["Outdoor & Hiking", "Footwear"],                                   "CAT-OUT",        None, false),
        new("CAT-OUT-FTW-SHOE",       ["Outdoor & Hiking", "Footwear", "Hiking shoes"],                   "CAT-OUT-FTW",    ["Upper", "Membrane", "Sole", "Weight"], false),
        new("CAT-OUT-NAV",            ["Outdoor & Hiking", "Navigation"],                                 "CAT-OUT",        None, false),
        new("CAT-OUT-NAV-SAT",        ["Outdoor & Hiking", "Navigation", "Satellite communicators"],      "CAT-OUT-NAV",    ["Network", "Battery life", "Water resistance", "Weight"], false),

        // ══ 3. Home Espresso ════════════════════════════════════════════════════════
        new("CAT-ESP",                ["Home Espresso"],                                                  null,             None, false),
        new("CAT-ESP-MAC",            ["Home Espresso", "Machines"],                                      "CAT-ESP",        None, false),
        new("CAT-ESP-MAC-ESP",        ["Home Espresso", "Machines", "Espresso machines"],                 "CAT-ESP-MAC",    ["Portafilter size", "Boiler", "Pump pressure", "Grinder", "Water tank"], false),
        new("CAT-ESP-GRI",            ["Home Espresso", "Grinders"],                                      "CAT-ESP",        None, false),
        new("CAT-ESP-GRI-ELE",        ["Home Espresso", "Grinders", "Electric burr grinders"],            "CAT-ESP-GRI",    ["Burr type", "Burr size", "Grind settings", "Hopper capacity"], false),
        new("CAT-ESP-GRI-HAND",       ["Home Espresso", "Grinders", "Hand grinders"],                     "CAT-ESP-GRI",    ["Burr type", "Burr size", "Grind settings", "Weight"], false),
        new("CAT-ESP-ACC",            ["Home Espresso", "Accessories"],                                   "CAT-ESP",        None, false),
        new("CAT-ESP-ACC-PF",         ["Home Espresso", "Accessories", "Portafilters"],                   "CAT-ESP-ACC",    ["Portafilter size", "Basket", "Handle material", "Type"], false),
        new("CAT-ESP-ACC-WDT",        ["Home Espresso", "Accessories", "Distribution tools"],             "CAT-ESP-ACC",    ["Portafilter size", "Needle count", "Needle diameter", "Material"], false),
        new("CAT-ESP-ACC-TAMP",       ["Home Espresso", "Accessories", "Tampers"],                        "CAT-ESP-ACC",    ["Tamper diameter", "Base", "Spring force", "Material"], false),
        new("CAT-ESP-COF",            ["Home Espresso", "Coffee"],                                        "CAT-ESP",        None, false),
        new("CAT-ESP-COF-BEAN",       ["Home Espresso", "Coffee", "Whole beans"],                         "CAT-ESP-COF",    ["Origin", "Roast", "Process", "Pack size", "Caffeine"], false),
        new("CAT-ESP-MNT",            ["Home Espresso", "Maintenance"],                                   "CAT-ESP",        None, false),
        new("CAT-ESP-MNT-CLEAN",      ["Home Espresso", "Maintenance", "Cleaning tablets"],               "CAT-ESP-MNT",    ["Use", "Pack size", "Dose", "Cycle"], false),
        new("CAT-ESP-MNT-DESC",       ["Home Espresso", "Maintenance", "Descaler"],                       "CAT-ESP-MNT",    ["Use", "Pack size", "Dose", "Cycle"], false),

        // ══ 4. Gaming ═══════════════════════════════════════════════════════════════
        new("CAT-GAM",                ["Gaming"],                                                         null,             None, false),
        new("CAT-GAM-CON",            ["Gaming", "Consoles"],                                             "CAT-GAM",        None, false),
        new("CAT-GAM-CON-HYB",        ["Gaming", "Consoles", "Handheld hybrid"],                          "CAT-GAM-CON",    ["Storage", "Display", "Docked resolution", "Controllers included"], false),
        new("CAT-GAM-GME",            ["Gaming", "Games"],                                                "CAT-GAM",        None, false),
        new("CAT-GAM-GME-RACE",       ["Gaming", "Games", "Racing"],                                      "CAT-GAM-GME",    ["Platform", "Genre", "Players", "Media"], false),
        new("CAT-GAM-GME-ADV",        ["Gaming", "Games", "Adventure"],                                   "CAT-GAM-GME",    ["Platform", "Genre", "Players", "Media"], false),
        new("CAT-GAM-CTL",            ["Gaming", "Controllers"],                                          "CAT-GAM",        None, false),
        new("CAT-GAM-CTL-CON",        ["Gaming", "Controllers", "Console controllers"],                   "CAT-GAM-CTL",    ["Platform", "Connection", "Battery life", "Weight"], false),
        new("CAT-GAM-AUD",            ["Gaming", "Audio"],                                                "CAT-GAM",        None, false),
        new("CAT-GAM-AUD-HS",         ["Gaming", "Audio", "Gaming headsets"],                             "CAT-GAM-AUD",    ["Connection", "Driver", "Battery life", "Microphone"], false),
        new("CAT-GAM-STO",            ["Gaming", "Storage"],                                              "CAT-GAM",        None, false),
        new("CAT-GAM-STO-CARD",       ["Gaming", "Storage", "Console memory cards"],                      "CAT-GAM-STO",    ["Card format", "Capacity", "Read speed", "Platform"], false),
        new("CAT-GAM-ACC",            ["Gaming", "Accessories"],                                          "CAT-GAM",        None, false),
        new("CAT-GAM-ACC-DOCK",       ["Gaming", "Accessories", "Docks"],                                 "CAT-GAM-ACC",    ["Output", "Power delivery", "Ports", "Weight"], false),

        // ══ 5. Kitchen & Small Appliances ═══════════════════════════════════════════
        new("CAT-KIT",                ["Kitchen & Small Appliances"],                                     null,             None, false),
        new("CAT-KIT-BLD",            ["Kitchen & Small Appliances", "Blenders"],                         "CAT-KIT",        None, false),
        new("CAT-KIT-BLD-HP",         ["Kitchen & Small Appliances", "Blenders", "High-performance blenders"], "CAT-KIT-BLD", ["Motor power", "Jug capacity", "Programmes", "Speed control"], false),
        new("CAT-KIT-BLD-CT",         ["Kitchen & Small Appliances", "Blenders", "Countertop blenders"],   "CAT-KIT-BLD",   ["Motor power", "Jug capacity", "Programmes", "Speed control"], false),
        new("CAT-KIT-BLD-PER",        ["Kitchen & Small Appliances", "Blenders", "Personal blenders"],     "CAT-KIT-BLD",   ["Motor power", "Jug capacity", "Programmes", "Speed control"], false),
        new("CAT-KIT-WAT",            ["Kitchen & Small Appliances", "Water filtration"],                  "CAT-KIT",       None, false),
        new("CAT-KIT-WAT-CART",       ["Kitchen & Small Appliances", "Water filtration", "Filter cartridges"], "CAT-KIT-WAT", ["Filter type", "Filter capacity", "Cartridge life", "Pack size"], false),
        new("CAT-KIT-STO",            ["Kitchen & Small Appliances", "Food storage"],                      "CAT-KIT",       None, false),
        new("CAT-KIT-STO-VAC",        ["Kitchen & Small Appliances", "Food storage", "Vacuum canisters"],  "CAT-KIT-STO",   ["Capacity", "Seal", "Material", "Valve"], false),
        new("CAT-KIT-SCL",            ["Kitchen & Small Appliances", "Kitchen scales"],                    "CAT-KIT",       None, false),
        new("CAT-KIT-SCL-PREC",       ["Kitchen & Small Appliances", "Kitchen scales", "Precision scales"], "CAT-KIT-SCL",  ["Readability", "Capacity", "Timer", "Power"], false),
        new("CAT-KIT-CKW",            ["Kitchen & Small Appliances", "Cookware"],                          "CAT-KIT",       None, false),
        new("CAT-KIT-CKW-PRES",       ["Kitchen & Small Appliances", "Cookware", "Pressure cookers"],      "CAT-KIT-CKW",   ["Capacity", "Material", "Pressure levels", "Hob compatibility"], false),
        new("CAT-KIT-CKW-STEAM",      ["Kitchen & Small Appliances", "Cookware", "Food steamers"],         "CAT-KIT-CKW",   ["Capacity", "Tiers", "Timer", "Power"], false),
        new("CAT-KIT-CTE",            ["Kitchen & Small Appliances", "Coffee & tea"],                      "CAT-KIT",       None, false),
        new("CAT-KIT-CTE-PITCH",      ["Kitchen & Small Appliances", "Coffee & tea", "Milk pitchers"],     "CAT-KIT-CTE",   ["Capacity", "Material", "Spout", "Weight"], false),

        // ══ 6. Cycling ══════════════════════════════════════════════════════════════
        new("CAT-CYC",                ["Cycling"],                                                        null,             None, false),
        new("CAT-CYC-BAG",            ["Cycling", "Bags"],                                                "CAT-CYC",        None, false),
        new("CAT-CYC-BAG-BAR",        ["Cycling", "Bags", "Handlebar bags"],                              "CAT-CYC-BAG",    ["Capacity", "Mounting", "Weather protection", "Weight"], false),
        new("CAT-CYC-BAG-FRAME",      ["Cycling", "Bags", "Frame bags"],                                  "CAT-CYC-BAG",    ["Capacity", "Mounting", "Weather protection", "Weight"], false),
        new("CAT-CYC-TRN",            ["Cycling", "Training"],                                            "CAT-CYC",        None, false),
        new("CAT-CYC-TRN-HR",         ["Cycling", "Training", "Heart-rate monitors"],                     "CAT-CYC-TRN",    ["Sensor", "Connectivity", "Battery life", "Water resistance"], false),
        new("CAT-CYC-CMP",            ["Cycling", "Computers"],                                           "CAT-CYC",        None, false),
        new("CAT-CYC-CMP-GPS",        ["Cycling", "Computers", "GPS bike computers"],                     "CAT-CYC-CMP",    ["Display", "Battery life", "Navigation", "Connectivity"], false),
        new("CAT-CYC-LGT",            ["Cycling", "Lighting"],                                            "CAT-CYC",        None, false),
        new("CAT-CYC-LGT-FRONT",      ["Cycling", "Lighting", "Front lights"],                            "CAT-CYC-LGT",    ["Max output", "Battery", "Burn time", "Mount"], false),
        new("CAT-CYC-TYR",            ["Cycling", "Tyres"],                                               "CAT-CYC",        None, false),
        new("CAT-CYC-TYR-ROAD",       ["Cycling", "Tyres", "Road tyres"],                                 "CAT-CYC-TYR",    ["Size", "Casing", "Type", "Weight"], false),
        new("CAT-CYC-HLM",            ["Cycling", "Helmets"],                                             "CAT-CYC",        None, false),
        new("CAT-CYC-HLM-ROAD",       ["Cycling", "Helmets", "Road helmets"],                             "CAT-CYC-HLM",    ["Standard", "Vents", "Retention", "Weight"], false),
        new("CAT-CYC-TOL",            ["Cycling", "Tools"],                                               "CAT-CYC",        None, false),
        new("CAT-CYC-TOL-MULTI",      ["Cycling", "Tools", "Multi-tools"],                                "CAT-CYC-TOL",    ["Functions", "Material", "Bit set", "Weight"], false),

        // ══ 7. Home Audio ═══════════════════════════════════════════════════════════
        new("CAT-AUD",                ["Home Audio"],                                                     null,             None, false),
        new("CAT-AUD-HP",             ["Home Audio", "Headphones"],                                       "CAT-AUD",        None, false),
        new("CAT-AUD-HP-OVER",        ["Home Audio", "Headphones", "Over-ear wireless"],                  "CAT-AUD-HP",     ["Driver", "Connection", "Noise cancelling", "Battery life", "Weight"], false),
        new("CAT-AUD-HP-IEM",         ["Home Audio", "Headphones", "In-ear monitors"],                    "CAT-AUD-HP",     ["Driver", "Connection", "Impedance", "Cable"], false),
        new("CAT-AUD-DAC",            ["Home Audio", "DACs"],                                             "CAT-AUD",        None, false),
        new("CAT-AUD-DAC-DESK",       ["Home Audio", "DACs", "Desktop DACs"],                             "CAT-AUD-DAC",    ["DAC chip", "Digital filter", "Inputs", "Outputs", "Sample rate"], false),
        new("CAT-AUD-SPK",            ["Home Audio", "Speakers"],                                         "CAT-AUD",        None, false),
        new("CAT-AUD-SPK-ACT",        ["Home Audio", "Speakers", "Active bookshelf"],                     "CAT-AUD-SPK",    ["Driver", "Amplifier power", "Inputs", "Streaming"], false),
        new("CAT-AUD-SPK-SMART",      ["Home Audio", "Speakers", "Smart speakers"],                       "CAT-AUD-SPK",    ["Driver", "Connection", "Voice assistant", "Streaming"], false),
        new("CAT-AUD-STR",            ["Home Audio", "Streamers"],                                        "CAT-AUD",        None, false),
        new("CAT-AUD-STR-NET",        ["Home Audio", "Streamers", "Network streamers"],                   "CAT-AUD-STR",    ["DAC chip", "Digital filter", "Outputs", "Streaming", "Sample rate"], false),

        // ══ 8. Power & Travel Tech ══════════════════════════════════════════════════
        new("CAT-PWR",                ["Power & Travel Tech"],                                            null,             None, false),
        new("CAT-PWR-BNK",            ["Power & Travel Tech", "Power banks"],                             "CAT-PWR",        None, false),
        new("CAT-PWR-BNK-HIGH",       ["Power & Travel Tech", "Power banks", "High-output power banks"],  "CAT-PWR-BNK",    ["Capacity", "Max output", "Ports", "Recharge time", "Weight"], false),
        new("CAT-PWR-BNK-LIGHT",      ["Power & Travel Tech", "Power banks", "Ultralight power banks"],   "CAT-PWR-BNK",    ["Capacity", "Max output", "Ports", "Recharge time", "Weight"], false),
        new("CAT-PWR-CBL",            ["Power & Travel Tech", "Cables"],                                  "CAT-PWR",        None, false),
        new("CAT-PWR-CBL-USBC",       ["Power & Travel Tech", "Cables", "USB-C cables"],                  "CAT-PWR-CBL",    ["Length", "Max power", "Data rate", "Connectors"], false),
        new("CAT-PWR-PRO",            ["Power & Travel Tech", "Protection"],                              "CAT-PWR",        None, false),
        new("CAT-PWR-PRO-DRY",        ["Power & Travel Tech", "Protection", "Dry bags"],                  "CAT-PWR-PRO",    ["Capacity", "Material", "Water resistance", "Closure", "Weight"], false),
        new("CAT-PWR-CHG",            ["Power & Travel Tech", "Chargers"],                                "CAT-PWR",        None, false),
        new("CAT-PWR-CHG-GAN",        ["Power & Travel Tech", "Chargers", "GaN wall chargers"],           "CAT-PWR-CHG",    ["Max output", "Ports", "Plug type", "Weight"], false),
        new("CAT-PWR-ADP",            ["Power & Travel Tech", "Adapters"],                                "CAT-PWR",        None, false),
        new("CAT-PWR-ADP-TRAVEL",     ["Power & Travel Tech", "Adapters", "Travel adapters"],             "CAT-PWR-ADP",    ["Regions", "Max output", "USB ports", "Weight"], false),

        // ══ 9. Health & Personal Care — the §0.5 / D-6 plant. SENSITIVE. ════════════
        //     Marked sensitive at the ROOT, so every descendant inherits it and a later
        //     leaf added here cannot quietly escape the suppression rule.
        new("CAT-HLT",                ["Health & Personal Care"],                                         null,             None, true),
        new("CAT-HLT-BP",             ["Health & Personal Care", "Blood pressure"],                       "CAT-HLT",        None, true),
        new("CAT-HLT-BP-ARM",         ["Health & Personal Care", "Blood pressure", "Upper-arm monitors"], "CAT-HLT-BP",     ["Measurement", "Cuff size", "Memory", "Connectivity"], true),
        new("CAT-HLT-BP-CUFF",        ["Health & Personal Care", "Blood pressure", "Cuffs"],              "CAT-HLT-BP",     ["Cuff size", "Compatibility", "Material", "Closure"], true),
        new("CAT-HLT-MED",            ["Health & Personal Care", "Medication management"],                "CAT-HLT",        None, true),
        new("CAT-HLT-MED-ORG",        ["Health & Personal Care", "Medication management", "Pill organisers"], "CAT-HLT-MED", ["Compartments", "Period", "Material", "Closure"], true),
        new("CAT-HLT-DIA",            ["Health & Personal Care", "Home diagnostics"],                     "CAT-HLT",        None, true),
        new("CAT-HLT-DIA-OXI",        ["Health & Personal Care", "Home diagnostics", "Pulse oximeters"],  "CAT-HLT-DIA",    ["Measurement", "Display", "Battery", "Weight"], true),

        // ══ EXTENSION NODES — the Eval 02 measurability extension. DECLARED. ═════════
        //
        //  No new ROOT department: every node below hangs off one of the nine already
        //  above, so "nine root departments" stays true and §B.1's eight-department
        //  product table stays checkable. What these add is LEAF SPACING — each new
        //  persona in Personas.cs needs its purchases to sit in distinct leaves, and each
        //  of its latent interests needs a reachable answer in a leaf it does NOT already
        //  own. Without the leaves there is nowhere for a cross-category answer to live,
        //  and the coverage metric measures a lookup instead of a discovery.
        //
        //  Sensitivity: nothing here is flagged, and nothing here sits under CAT-HLT. A
        //  leaf added under Health would inherit the flag automatically (see Catalogue's
        //  IsSensitiveNode); these deliberately do not go there.

        // — Photography ————————————————————————————————————————————————————————————
        new("CAT-PHO-MEM-READ",       ["Photography", "Memory", "Card readers"],                          "CAT-PHO-MEM",    ["Card format", "Interface", "Read speed", "Weight"], false),

        // — Outdoor & Hiking ———————————————————————————————————————————————————————
        new("CAT-OUT-BAG-VEST",       ["Outdoor & Hiking", "Backpacks", "Running vests"],                 "CAT-OUT-BAG",    ["Capacity", "Fit", "Flask compatibility", "Weight"], false),
        new("CAT-OUT-NAV-WATCH",      ["Outdoor & Hiking", "Navigation", "Trail watches"],                "CAT-OUT-NAV",    ["Display", "Battery life", "Navigation", "Weight"], false),

        // — Home Espresso ——————————————————————————————————————————————————————————
        new("CAT-ESP-MNT-BRUSH",      ["Home Espresso", "Maintenance", "Group brushes"],                  "CAT-ESP-MNT",    ["Use", "Bristle material", "Handle material", "Length"], false),

        // — Gaming —————————————————————————————————————————————————————————————————
        new("CAT-GAM-GME-PARTY",      ["Gaming", "Games", "Party"],                                       "CAT-GAM-GME",    ["Platform", "Genre", "Players", "Media"], false),
        new("CAT-GAM-ACC-CASE",       ["Gaming", "Accessories", "Carry cases"],                           "CAT-GAM-ACC",    ["Compatibility", "Material", "Capacity", "Weight"], false),

        // — Kitchen & Small Appliances ——————————————————————————————————————————————
        new("CAT-KIT-CTE-THERM",      ["Kitchen & Small Appliances", "Coffee & tea", "Milk thermometers"], "CAT-KIT-CTE",   ["Range", "Readability", "Probe length", "Mount"], false),
        new("CAT-KIT-CTE-FROTH",      ["Kitchen & Small Appliances", "Coffee & tea", "Milk frothers"],     "CAT-KIT-CTE",   ["Type", "Power", "Capacity", "Weight"], false),
        new("CAT-KIT-CTE-CUP",        ["Kitchen & Small Appliances", "Coffee & tea", "Espresso cups"],     "CAT-KIT-CTE",   ["Capacity", "Material", "Pieces", "Dishwasher safe"], false),
        new("CAT-KIT-WAT-JUG",        ["Kitchen & Small Appliances", "Water filtration", "Filter jugs"],   "CAT-KIT-WAT",   ["Capacity", "Filter type", "Cartridge life", "Material"], false),
        new("CAT-KIT-WAT-MIN",        ["Kitchen & Small Appliances", "Water filtration", "Brewing minerals"], "CAT-KIT-WAT", ["Use", "Pack size", "Dose", "Composition"], false),
        new("CAT-KIT-STO-SEAL",       ["Kitchen & Small Appliances", "Food storage", "Vacuum sealers"],    "CAT-KIT-STO",   ["Sealing width", "Pump", "Bag compatibility", "Power"], false),

        // — Cycling ————————————————————————————————————————————————————————————————
        new("CAT-CYC-LGT-REAR",       ["Cycling", "Lighting", "Rear lights"],                             "CAT-CYC-LGT",    ["Max output", "Battery", "Burn time", "Mount"], false),
        new("CAT-CYC-APP",            ["Cycling", "Apparel"],                                             "CAT-CYC",        None, false),
        new("CAT-CYC-APP-OVER",       ["Cycling", "Apparel", "Overshoes"],                                "CAT-CYC-APP",    ["Material", "Closure", "Sizes", "Weight"], false),
        new("CAT-CYC-MUD",            ["Cycling", "Mudguards"],                                           "CAT-CYC",        None, false),
        new("CAT-CYC-MUD-FULL",       ["Cycling", "Mudguards", "Full-length mudguards"],                  "CAT-CYC-MUD",    ["Tyre clearance", "Material", "Mounting", "Weight"], false),
        new("CAT-CYC-TRN-SMART",      ["Cycling", "Training", "Smart trainers"],                          "CAT-CYC-TRN",    ["Resistance", "Max power", "Connectivity", "Accuracy"], false),

        // — Home Audio —————————————————————————————————————————————————————————————
        new("CAT-AUD-PWR",            ["Home Audio", "Power"],                                            "CAT-AUD",        None, false),
        new("CAT-AUD-PWR-LIN",        ["Home Audio", "Power", "Linear power supplies"],                   "CAT-AUD-PWR",    ["Output voltage", "Output current", "Noise floor", "Connector"], false),
        new("CAT-AUD-DAC-PORT",       ["Home Audio", "DACs", "Portable DACs"],                            "CAT-AUD-DAC",    ["DAC chip", "Inputs", "Outputs", "Sample rate", "Weight"], false),
        new("CAT-AUD-SPK-STAND",      ["Home Audio", "Speakers", "Speaker stands"],                       "CAT-AUD-SPK",    ["Height", "Material", "Top plate", "Fill"], false),
        new("CAT-AUD-HP-WIRED",       ["Home Audio", "Headphones", "Over-ear wired"],                     "CAT-AUD-HP",     ["Driver", "Connection", "Impedance", "Weight"], false),
        new("CAT-AUD-CBL",            ["Home Audio", "Cables"],                                           "CAT-AUD",        None, false),
        new("CAT-AUD-CBL-RCA",        ["Home Audio", "Cables", "Analogue interconnects"],                 "CAT-AUD-CBL",    ["Length", "Connectors", "Conductor", "Shielding"], false),

        // — Power & Travel Tech ————————————————————————————————————————————————————
        new("CAT-PWR-CHG-SOLAR",      ["Power & Travel Tech", "Chargers", "Solar chargers"],              "CAT-PWR-CHG",    ["Peak output", "Panel area", "Ports", "Weight"], false),
    ];

    /// <summary>
    /// The special-category TERM set screened at the OUTPUT layer, per §0.5 / D-6. The
    /// category flag above blocks the channel a naive system uses; this list blocks the
    /// one the regulator cares about — an emitted interest label or reason string that
    /// NAMES a special category, even when every product involved sits in an ordinary
    /// department. Target's pregnancy inference was assembled from unscented lotion,
    /// cotton balls, magnesium and a large handbag; not one of those is in a sensitive
    /// category, so a category-only control would have caught none of it.
    /// </summary>
    /// <remarks>
    /// Matching is a case-insensitive substring test on the normalised label, and it is
    /// the guardrail lane (<c>SensitiveInferenceBlocklist</c>) that applies it — this
    /// class only owns the vocabulary. §F.5's list is included verbatim and extended with
    /// the terms an inference would actually use, because a blocklist of category NAMES
    /// cannot catch an inference phrased in plain language.
    /// </remarks>
    public static IReadOnlyList<string> BlockedInferenceTerms { get; } =
    [
        // §F.5, verbatim
        "health", "pharmacy", "medical device", "fertility", "pregnancy", "baby",
        "love + play", "religion", "politics", "trade union", "ethnic origin", "biometrics",
        // The plain-language forms an inferred label would actually take (D-6)
        "blood pressure", "hypertension", "cardiovascular", "heart condition", "cardiac",
        "diabetes", "medication", "prescription", "symptom", "diagnosis", "diagnosed",
        "condition", "therapy", "treatment", "clinical", "patient", "cholesterol",
        "low-sodium diet", "doctor", "illness", "disease", "disorder",
    ];

    /// <summary>Number of nodes in the tree. Printed by the catalogue self-check panel.</summary>
    public static int NodeCount => All.Count;
}
