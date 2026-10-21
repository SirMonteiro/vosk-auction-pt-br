"""Differential parity check: C# parser vs the Python reference.

The 50-row fixture suite is the acceptance gate, but it cannot cover every token
combination the game will hear. This tool generates a large corpus of utterances from the
domain tables, runs both implementations, and diffs the results field by field.

Usage::

    uv run python -m tools.check_csharp_parity              # ~20k generated utterances
    uv run python -m tools.check_csharp_parity --quick      # fixture + hand-picked only

Exit code 1 when any utterance disagrees, with the first 20 printed.
"""

from __future__ import annotations

import argparse
import json
import random
import subprocess
import sys
from pathlib import Path

from auction_demo.config import PROJECT_ROOT
from auction_demo.nlu.lexicon import (
    BINGO,
    CHANT_PHRASES,
    CORRECT_PHRASES,
    DELTA_MARKERS,
    FILLERS,
    MAGNITUDES,
    MULTIPLIER_WORDS,
    PASS_PHRASES,
    QUERY_PHRASES,
)
from auction_demo.nlu.parser import parse

CS_DIR = PROJECT_ROOT / "Packages" / "com.auction.voice"
BUILD = PROJECT_ROOT / ".toolchain" / "build"
DOTNET = PROJECT_ROOT / ".toolchain" / "dotnet" / "dotnet"
CSC = DOTNET.parent / "sdk" / "8.0.426" / "Roslyn" / "bincore" / "csc.dll"
REF = DOTNET.parent / "packs" / "NETStandard.Library.Ref" / "2.1.0" / "ref" / "netstandard2.1"

PROBE_SOURCE = r"""
using System;
using System.Collections.Generic;
using System.IO;
using AuctionVoice.Core;
using AuctionVoice.NLU;

public static class ParityProbe
{
    public static int Main(string[] args)
    {
        var parser = new AuctionBidParser();
        string line;
        while ((line = Console.In.ReadLine()) != null)
        {
            BidResult r = parser.Parse(line);
            var numbers = new List<string>();
            foreach (double v in r.NumbersFound)
            {
                numbers.Add(v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            var idioms = new List<string>();
            foreach (string v in r.IdiomsFound) { idioms.Add(v); }
            var payload = new Dictionary<string, object>
            {
                { "value", r.Value },
                { "mode", r.Mode.ToWire() },
                { "kind", r.Kind.ToWire() },
                { "action", r.Action.ToWire() },
                { "has_addition", r.HasAddition },
                { "has_multiplication", r.HasMultiplication },
                { "currency", r.Currency },
                { "confidence", r.Confidence },
                { "reason", r.Reason },
                { "closing", r.Closing },
                { "target_value", r.TargetValue.HasValue ? (object)r.TargetValue.Value : null },
                { "lot", r.Lot.HasValue ? (object)r.Lot.Value : null },
                { "numbers", numbers },
                { "idioms", idioms },
            };
            Console.Out.WriteLine(MiniJson.Serialize(payload));
        }
        return 0;
    }
}

internal static class MiniJson
{
    public static string Serialize(object value)
    {
        var sb = new System.Text.StringBuilder();
        Write(sb, value);
        return sb.ToString();
    }

    private static void Write(System.Text.StringBuilder sb, object value)
    {
        if (value == null) { sb.Append("null"); return; }
        if (value is bool b) { sb.Append(b ? "true" : "false"); return; }
        if (value is string s) { WriteString(sb, s); return; }
        if (value is double d)
        {
            sb.Append(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            return;
        }
        if (value is int i)
        {
            sb.Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return;
        }
        if (value is System.Collections.IDictionary dict)
        {
            sb.Append('{');
            bool first = true;
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                if (!first) { sb.Append(','); }
                first = false;
                WriteString(sb, (string)entry.Key);
                sb.Append(':');
                Write(sb, entry.Value);
            }
            sb.Append('}');
            return;
        }
        if (value is System.Collections.IEnumerable list)
        {
            sb.Append('[');
            bool first = true;
            foreach (object item in list)
            {
                if (!first) { sb.Append(','); }
                first = false;
                Write(sb, item);
            }
            sb.Append(']');
            return;
        }
        WriteString(sb, value.ToString());
    }

    private static void WriteString(System.Text.StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) { sb.Append("\\u").Append(((int)c).ToString("x4")); }
                    else { sb.Append(c); }
                    break;
            }
        }
        sb.Append('"');
    }
}
"""


def build_probe() -> Path:
    """Compile the C# package plus the parity probe."""
    probe_dir = BUILD / "parity"
    probe_dir.mkdir(parents=True, exist_ok=True)
    source = probe_dir / "ParityProbe.cs"
    source.write_text(PROBE_SOURCE, encoding="utf-8")

    dll = probe_dir / "AuctionVoice.ParityProbe.dll"
    # Only the Unity-free NLU surface is needed: Core, NLU and the logger interface.
    sources = sorted(
        str(p)
        for pattern in ("Core", "NLU")
        for p in (CS_DIR / "Runtime" / pattern).rglob("*.cs")
    )
    sources.append(str(CS_DIR / "Runtime" / "Diagnostics" / "IAuctionLogger.cs"))
    refs = [f"-r:{p}" for p in sorted(REF.glob("*.dll"))]
    subprocess.run(
        [
            str(DOTNET), "exec", str(CSC), "-nologo", "-langversion:9.0", "-target:exe",
            "-warn:0", f"-out:{dll}", "-nostdlib+", *refs, *sources, str(source),
        ],
        check=True,
    )
    (probe_dir / "AuctionVoice.ParityProbe.runtimeconfig.json").write_text(
        json.dumps(
            {
                "runtimeOptions": {
                    "tfm": "net8.0",
                    "framework": {"name": "Microsoft.NETCore.App", "version": "8.0.0"},
                }
            }
        ),
        encoding="utf-8",
    )
    return dll


def run_csharp(dll: Path, utterances: list[str]) -> list[dict]:
    """Parse every utterance with the C# implementation."""
    payload = "\n".join(utterances) + "\n"
    result = subprocess.run(
        [str(DOTNET), "exec", str(dll)],
        input=payload.encode("utf-8"),
        capture_output=True,
        check=True,
    )
    lines = result.stdout.decode("utf-8").splitlines()
    return [json.loads(line) if line.strip() else {} for line in lines]


def corpus(quick: bool) -> list[str]:
    """Build the differential corpus."""
    cases = [
        json.loads(line)["text"]
        for line in (PROJECT_ROOT / "tests" / "fixtures" / "text_cases.jsonl")
        .read_text(encoding="utf-8")
        .splitlines()
        if line.strip()
    ]

    handpicked = [
        # spec section 3.4 / 3.5 and section 9 forms
        "quarenta e sete", "cento e vinte e cinco", "mil e quinhentos", "mil quinhentos",
        "dois mil e quinhentos", "quarenta e sete mil e quinhentos",
        "nove mil novecentos e noventa e nove", "um milhão e duzentos mil", "meio milhão",
        "um milhão e meio", "duas onças", "onça e meia", "meia dúzia", "uma dúzia",
        "dúzia e meia", "um barão", "três placas", "meio pau", "cinquenta pila", "cem conto",
        "cinquenta reais e vinte centavos", "sete seis", "cinco zero zero",
        "trinta e dez", "doze e três", "50, 40, 20", "cinquenta, cinquenta, cinquenta",
        "quero", "lote um", "cinquenta e fechando", "cinquenta em cima", "sobe mais",
        "duas dezenas", "meia centena", "um milheiro", "um par de onças", "errei, era cinquenta",
        "duas notas de cem", "cinquenta na lata", "cinquenta reais na lata",
        "mil em cima", "cinquenta redondo", "sem troco", "pelo valor de cem",
        "vou de duzentos", "cinquenta pra levar", "cinquenta na mão",
        "noventa e nove", "cento e cinquenta", "duzentos e cinquenta mil",
        "um milhão", "dois mi", "cinquenta k", "cinquenta ca",
        # intents
        "passo", "tô fora", "desisto", "sem lance", "não quero", "deixa passar",
        "não dou", "abandono", "saio", "na minha vez não",
        "errei", "corrige", "cancela", "anula", "volta", "desconsidera", "falei errado",
        "quanto tá", "qual o lance", "qual é o valor", "tá em quanto", "repete o lance",
        "dou-lhe uma", "dou-lhe duas", "dou-lhe três", "vendido", "arrematado",
        "bateu o martelo", "ninguém dá mais",
        "lote três", "lote 2", "segundo lote", "quinto lote", "primeiro lote",
        # multipliers
        "dobro", "triplo", "quádruplo", "quíntuplo", "metade", "duplico", "triplico",
        "dobro de cem", "triplo de cinquenta", "metade de cem", "quádruplo de vinte",
        # repairs
        "sixenta", "sisenta", "seicenta", "novem mil", "noventenove", "noventa em nove",
        "mas vinte", "sem reais", "mew zom", "muzinho", "vindes", "vídeo", "vite",
        "cinco itinhas", "duza então", "quinza então", "tresentão", "seissentos",
        # empty / odd
        "", "   ", "bananas", "cinquenta bananas", "a", "e",
    ]

    if quick:
        return cases + handpicked

    rng = random.Random(20261020)
    generated: list[str] = []

    # Numbers 0-999 in every common spoken form, alone and framed.
    units = ["um", "dois", "três", "quatro", "cinco", "seis", "sete", "oito", "nove"]
    teens = ["dez", "onze", "doze", "treze", "quatorze", "quinze", "dezesseis", "dezessete",
             "dezoito", "dezenove"]
    tens = ["vinte", "trinta", "quarenta", "cinquenta", "sessenta", "setenta", "oitenta", "noventa"]
    hundreds = ["cem", "cento", "duzentos", "trezentos", "quatrocentos", "quinhentos",
                "seiscentos", "setecentos", "oitocentos", "novecentos"]

    numbers: list[str] = []
    numbers.extend(units + teens + tens + hundreds)
    numbers.extend(f"{ten} e {unit}" for ten in tens for unit in units)
    numbers.extend(f"{hundred} e {below}" for hundred in hundreds[:1] for below in tens)
    numbers.extend(f"{value} mil" for value in (units + teens + tens + ["cem", "duzentos"]))
    numbers.extend(f"{value} mil e {tail}" for value in ("um", "dois", "cinco", "dez", "cem")
                   for tail in ("quinhentos", "duzentos", "cinquenta"))

    for number in numbers:
        generated.append(number)
        generated.append(f"mais {number}")
        generated.append(f"cobre {number}")
        generated.append(f"{number} reais")
        generated.append(f"{number} mais {number}")

    # Slang and idioms.
    for phrase in list(MAGNITUDES) + list(BINGO):
        generated.append(phrase)
        generated.append(f"um {phrase}")
        generated.append(f"mais {phrase}")

    # Intents with filler noise around them.
    for phrase in (
        list(PASS_PHRASES) + list(CORRECT_PHRASES) + list(QUERY_PHRASES) + list(CHANT_PHRASES)
    ):
        generated.append(phrase)
        generated.append(f"olha {phrase}")

    for marker in DELTA_MARKERS:
        generated.append(f"{marker} cinquenta")
    for filler in FILLERS:
        generated.append(f"cinquenta {filler}")
    for word in MULTIPLIER_WORDS:
        generated.append(word)
        generated.append(f"{word} de cem")

    # Random combinations of domain vocabulary.
    vocabulary = (
        list(units) + list(teens) + list(tens) + ["cem", "cento", "mil", "milhão"]
        + ["mais", "e", "de", "reais", "conto", "pila", "barão", "pau", "placa",
           "onça", "lobo", "garça", "dúzia", "meia", "meio", "redondo", "na", "mão",
           "dobro", "triplo", "metade", "passo", "errei", "lote", "um", "duas"]
    )
    for _ in range(4000):
        length = rng.randint(1, 5)
        generated.append(" ".join(rng.choice(vocabulary) for _ in range(length)))

    # Deduplicate while keeping order.
    seen: set[str] = set()
    unique: list[str] = []
    for utterance in [*cases, *handpicked, *generated]:
        if utterance not in seen:
            seen.add(utterance)
            unique.append(utterance)
    return unique


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--quick", action="store_true", help="fixtures + hand-picked only")
    parser.add_argument("--limit", type=int, default=0, help="cap the corpus size")
    args = parser.parse_args(argv)

    if not CSC.is_file():
        print(f"error: C# compiler not found at {CSC}", file=sys.stderr)
        print("Run tools/verify_csharp.sh once to bootstrap.", file=sys.stderr)
        return 2

    utterances = corpus(args.quick)
    if args.limit:
        utterances = utterances[: args.limit]

    dll = build_probe()
    actual = run_csharp(dll, utterances)

    if len(actual) != len(utterances):
        print(f"error: expected {len(utterances)} results, got {len(actual)}", file=sys.stderr)
        return 2

    mismatches: list[str] = []
    for utterance, cs in zip(utterances, actual, strict=True):
        py = parse(utterance)
        problems: list[str] = []

        if abs(py.value - cs["value"]) >= 0.01:
            problems.append(f"value py={py.value} cs={cs['value']}")
        if py.value_mode != cs["mode"]:
            problems.append(f"mode py={py.value_mode} cs={cs['mode']}")
        if py.bid_kind != cs["kind"]:
            problems.append(f"kind py={py.bid_kind} cs={cs['kind']}")
        if py.action_type != cs["action"]:
            problems.append(f"action py={py.action_type} cs={cs['action']}")
        if py.has_addition != cs["has_addition"]:
            problems.append(f"has_addition py={py.has_addition} cs={cs['has_addition']}")
        if py.has_multiplication != cs["has_multiplication"]:
            problems.append(f"has_multiplication py={py.has_multiplication} cs={cs['has_multiplication']}")
        if py.currency != cs["currency"]:
            problems.append(f"currency py={py.currency!r} cs={cs['currency']!r}")
        if abs(py.confidence - cs["confidence"]) >= 0.001:
            problems.append(f"confidence py={py.confidence} cs={cs['confidence']}")
        if py.reason != cs["reason"]:
            problems.append(f"reason py={py.reason!r} cs={cs['reason']!r}")
        if py.closing != cs["closing"]:
            problems.append(f"closing py={py.closing} cs={cs['closing']}")
        if py.target_value != cs["target_value"]:
            problems.append(f"target_value py={py.target_value} cs={cs['target_value']}")
        if py.lot != cs["lot"]:
            problems.append(f"lot py={py.lot} cs={cs['lot']}")
        if [round(v, 3) for v in py.numbers_found] != [round(float(v), 3) for v in cs["numbers"]]:
            problems.append(f"numbers py={py.numbers_found} cs={cs['numbers']}")
        if py.idioms_found != cs["idioms"]:
            problems.append(f"idioms py={py.idioms_found} cs={cs['idioms']}")

        if problems:
            mismatches.append(f"{utterance!r}: " + "; ".join(problems))

    if mismatches:
        print(f"{len(mismatches)} / {len(utterances)} utterances disagree\n", file=sys.stderr)
        for line in mismatches[:20]:
            print("  " + line, file=sys.stderr)
        if len(mismatches) > 20:
            print(f"  ... and {len(mismatches) - 20} more", file=sys.stderr)
        return 1

    print(f"parity ok: {len(utterances)} utterances, every field identical")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
