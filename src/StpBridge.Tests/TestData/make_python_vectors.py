"""Regenerate python_vectors.json: Python's own answers for the C# parity tests.

    python -I src/StpBridge.Tests/TestData/make_python_vectors.py

Everything is computed by the running CPython (json, decimal, datetime) and by the real
``stp_v2_apply_contract`` (load_apply_package -> apply_package_to_json_value -> canonical
dumps), so the C# tests compare against Python, not against hand-typed expectations.
Deterministic (fixed random seed). All data is synthetic.
"""

from __future__ import annotations

import base64
import copy
import hashlib
import json
import random
import struct
import sys
from datetime import datetime
from decimal import Decimal
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
sys.path.insert(0, str(REPO / "python"))
import stp_core  # noqa: E402,F401
from stp_v2_apply_contract import apply_package_to_json_value, load_apply_package  # noqa: E402


def canonical(value) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode("utf-8")


def float_vectors() -> list[dict]:
    rng = random.Random(20261119)
    values = [
        0.0, -0.0, 1.0, -1.0, 0.1, 0.2, 0.1 + 0.2, 1 / 3, 2 / 3, 100.0, 1e15, 1e16, 1e17, 123456789012345680.0,
        9007199254740993.0, 1e-4, 1e-5, 0.00012345, 1.5e-7, 5e-324, 2.2250738585072014e-308,
        2.2250738585072009e-308, 1.7976931348623157e308, 4.35, 2.675, 1e22, 1e23, 8.41e21, 5e-310,
        0.30000000000000004, 26.6666, 33.3334, 40.0, 12.5, 1e100, 1.2345678901234567e-300,
    ]
    for exponent in range(-1074, 1024, 7):
        values.append(2.0 ** exponent if exponent > -1075 else 5e-324)
    for power in range(-30, 30):
        values.append(float(f"1e{power}"))
        values.append(float(f"9.999999999999999e{power}"))
    for _ in range(3000):
        bits = rng.getrandbits(64)
        value = struct.unpack("<d", struct.pack("<Q", bits))[0]
        if value != value or value in (float("inf"), float("-inf")):
            continue
        values.append(value)
    for _ in range(1000):
        values.append(round(rng.uniform(-1000, 1000), rng.randint(0, 6)))
    result = []
    for value in values:
        bits = struct.unpack("<q", struct.pack("<d", value))[0]
        result.append({"bits": str(bits), "repr": repr(value), "json": json.dumps(value)})
    return result


def decimal_vectors() -> list[dict]:
    texts = [
        "0", "-0", "0.0", "-0.00", "0E+2", "0E-3", "1", "1.0", "1.50", "40", "4E+1", "1E+2", "1e2", "123E-2",
        "1E-7", "0.0000001", "0.000001", "1e-6", "-1_000.5", "  7 ", "26.6666", "33.3334", "100.000",
        "12345678901234567890.123456789", ".5", "5.", "-.25", "+3.25", "1E+30", "1.23456E-10", "9" * 40,
        "1_2_3", "0.1E1", "100E-2",
    ]
    result = []
    for text in texts:
        value = Decimal(text)
        result.append({"text": text, "str": str(value), "fixed": format(value, "f")})
    invalid = ["", " ", "NaN", "Infinity", "-inf", "sNaN", "1_", "_1", "1__0", "1e", "e5", "1.2.3", "0x10", "1,5", "--1", "1e+"]
    for text in invalid:
        try:
            value = Decimal(text)
            ok = value.is_finite()
        except Exception:  # noqa: BLE001
            ok = False
        result.append({
            "text": text,
            "str": str(Decimal(text)) if ok else None,
            "fixed": format(Decimal(text), "f") if ok else None,
        })
    return result


def datetime_vectors() -> list[dict]:
    texts = [
        "2026-03-02T08:00:00", "2026-03-02T08:00", "2026-03-02T08", "2026-03-02T08:00:00.5",
        "2026-03-02T08:00:00.123456", "2026-03-02T08:00:00.000100", "2026-03-02", "2026-02-29T00:00:00",
        "2028-02-29T23:59:59.999999", "0001-01-01T00:00:00",
    ]
    result = []
    for text in texts:
        try:
            value = datetime.fromisoformat(text)
            micro = value.microsecond
            snapshot = value.isoformat(timespec="microseconds").rstrip("0").rstrip(".") if micro else value.isoformat(timespec="seconds")
            result.append({"text": text, "isoformat": value.isoformat(), "snapshot": snapshot})
        except ValueError:
            result.append({"text": text, "isoformat": None, "snapshot": None})
    return result


def canonical_vectors() -> list[dict]:
    # Inputs are JSON texts; expected = Python canonical bytes (base64, exact).
    inputs = [
        '{"b":1,"a":2,"c":{"z":[],"y":{},"x":[{}]}}',
        '{"é":"ü","e":"ñ","E":"Ω","~":"~"}',
        '{"\\uffff":1,"\\ud83d\\ude00":2,"\\ue000":3,"\\ud7ff":4}',
        '{"s":"\\u0000\\u0001\\u001f\\u007f\\b\\f\\n\\r\\t\\"\\\\/"}',
        '{"s":"line\\u2028sep\\u2029end","t":"\\ud834\\udd1e music"}',
        '[12345678901234567890123456789012345678901234567890,-98765432109876543210,0,-0,9223372036854775807,9223372036854775808]',
        '[1.0,-0.0,0.5,1e16,1e-5,1E2,3.14159,2.5e-308,1.7976931348623157e308,100,1.0e0]',
        '{"nested":{"deeper":{"deepest":{}}},"list":[[],[[]],[{}],{"a":[]}]}',
        '[true,false,null,"",{"":""}]',
        '{"a":"<script>&amp;</script>","b":"\'single\'"}',
    ]
    return [{"input": text, "canonical_b64": base64.b64encode(canonical(json.loads(text))).decode("ascii")} for text in inputs]


def apply_package_vectors() -> list[dict]:
    golden = REPO / "tools" / "golden" / "expected" / "chain_fs.daily_tail_insert.apply.canonical.json"
    base = json.loads(golden.read_text(encoding="utf-8"))
    cases: list[tuple[str, object]] = [("golden", base)]

    def variant(name, mutate):
        value = copy.deepcopy(base)
        mutate(value)
        cases.append((name, value))

    fragment = lambda v: v["activities"][0]["fragments"][0]  # noqa: E731
    variant("date_only_horizon", lambda v: v.update(horizon_start=v["horizon_start"][:10]))
    variant("minute_precision_start", lambda v: fragment(v).update(start=fragment(v)["start"][:16]))
    variant("microsecond_start", lambda v: fragment(v).update(start=fragment(v)["start"][:19] + ".500000"))
    variant("decimal_exponent", lambda v: fragment(v).update(quantity_percentage="2.66666E+1"))
    variant("decimal_trailing_zero", lambda v: fragment(v).update(quantity_percentage="26.66660"))
    variant("decimal_whitespace", lambda v: fragment(v).update(quantity_percentage=" 26.6666 "))
    variant("unicode_ids", lambda v: fragment(v).update(native_bar_xml_key="x1-Ñandú-𝄞"))
    variant("missing_field", lambda v: fragment(v).pop("locked_completed"))
    variant("extra_field", lambda v: fragment(v).update(extra=1))
    variant("bool_as_string", lambda v: fragment(v).update(locked_completed="false"))
    variant("int_quantity", lambda v: fragment(v).update(quantity_percentage=40))
    variant("empty_string", lambda v: fragment(v).update(native_bar_key=""))
    variant("bad_provenance", lambda v: fragment(v).update(provenance="moved"))
    variant("aware_datetime", lambda v: fragment(v).update(start=fragment(v)["start"] + "+00:00"))
    variant("nan_quantity", lambda v: fragment(v).update(quantity_percentage="NaN"))
    variant("horizon_not_midnight", lambda v: v.update(horizon_start=v["horizon_start"][:11] + "01:00:00"))
    variant("horizon_reversed", lambda v: v.update(horizon_start=v["horizon_finish"], horizon_finish=v["horizon_start"]))
    variant("empty_fragments", lambda v: v["activities"][0].update(fragments=[]))
    variant("empty_activities", lambda v: v.update(activities=[]))
    variant("duplicate_activity", lambda v: v["activities"].append(copy.deepcopy(v["activities"][0])))
    variant("wrong_schema", lambda v: v.update(schema="vgs-stp-apply/2"))
    variant("logical_unit_date_bad", lambda v: fragment(v).update(logical_unit_date="2026-02-30"))

    result = []
    for name, value in cases:
        try:
            package = load_apply_package(value)
            encoded = canonical(apply_package_to_json_value(package))
            result.append({
                "name": name, "input": json.dumps(value, ensure_ascii=False),
                "canonical": encoded.decode("utf-8"), "sha256": hashlib.sha256(encoded).hexdigest(),
            })
        except (TypeError, ValueError, KeyError):
            result.append({"name": name, "input": json.dumps(value, ensure_ascii=False), "canonical": None, "sha256": None})
    return result


def main() -> int:
    vectors = {
        "python": sys.version.split()[0],
        "floats": float_vectors(),
        "decimals": decimal_vectors(),
        "datetimes": datetime_vectors(),
        "canonical": canonical_vectors(),
        "applyPackages": apply_package_vectors(),
    }
    target = HERE / "python_vectors.json"
    target.write_text(json.dumps(vectors, ensure_ascii=False, indent=1) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {target} ({len(vectors['floats'])} floats, {len(vectors['applyPackages'])} packages)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
