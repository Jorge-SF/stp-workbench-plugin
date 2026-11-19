"""Read-only native Excel export for the visible STP presentation."""

from __future__ import annotations

import base64
from io import BytesIO
import math
import re
from typing import Any

SCHEMA = "vgs-stp-excel-export/1"
RED = "#D05750"
GREEN = "#68BD79"
COLOR = re.compile(r"^#[0-9A-Fa-f]{6}$")
PNG_DATA_URL = re.compile(r"^data:image/png;base64,([A-Za-z0-9+/]+={0,2})$")


def _text(value: object, maximum: int = 160) -> str:
    return value.strip() if isinstance(value, str) and len(value.strip()) <= maximum else ""


def _number(value: object) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValueError("invalid numeric export value")
    return float(value)


def _color(value: object, fallback: str = "#FFFFFF") -> str:
    return value if isinstance(value, str) and COLOR.fullmatch(value) else fallback


def _chart_images(value: object) -> list[dict[str, Any]]:
    if not isinstance(value, list) or len(value) > 32:
        raise ValueError("invalid Excel export")
    images: list[dict[str, Any]] = []
    for item in value:
        if not isinstance(item, dict) or set(item) != {"title", "pngDataUrl"}:
            raise ValueError("invalid Excel export")
        match = PNG_DATA_URL.fullmatch(item["pngDataUrl"])
        if not match:
            raise ValueError("invalid Excel export")
        try:
            png = base64.b64decode(match.group(1), validate=True)
        except ValueError as error:
            raise ValueError("invalid Excel export") from error
        if not 8 <= len(png) <= 3_000_000 or not png.startswith(b"\x89PNG\r\n\x1a\n"):
            raise ValueError("invalid Excel export")
        images.append({"title": _text(item["title"], 120), "png": png})
    return images


def validate_excel_export_payload(payload: object) -> dict[str, Any]:
    if not isinstance(payload, dict) or payload.get("schema") != SCHEMA:
        raise ValueError("invalid Excel export")
    required = {"schema", "projectKey", "title", "rangeLabel", "actualProgressMode", "days", "activities", "aggregates", "chartTypes", "chartImages", "legend"}
    if set(payload) != required or not re.fullmatch(r"[0-9a-f]{64}", str(payload.get("projectKey", ""))):
        raise ValueError("invalid Excel export")
    days = payload["days"]
    if not isinstance(days, list) or not 1 <= len(days) <= 31:
        raise ValueError("invalid Excel export")
    normalized_days = []
    for day in days:
        if not isinstance(day, dict) or set(day) != {"date", "label"} or not re.fullmatch(r"\d{4}-\d{2}-\d{2}", str(day["date"])):
            raise ValueError("invalid Excel export")
        normalized_days.append({"date": day["date"], "label": _text(day["label"], 32)})
    def series(values: object) -> list[float | None]:
        if not isinstance(values, list) or len(values) != len(days): raise ValueError("invalid Excel export")
        return [None if value is None else _number(value) for value in values]
    activities = []
    if not isinstance(payload["activities"], list) or len(payload["activities"]) > 1000: raise ValueError("invalid Excel export")
    for row in payload["activities"]:
        if not isinstance(row, dict) or set(row) != {"id", "name", "type", "resource", "planned", "actual", "color"}: raise ValueError("invalid Excel export")
        activities.append({**{key: _text(row[key]) for key in ("id", "name", "type", "resource")}, "planned": series(row["planned"]), "actual": series(row["actual"]), "color": _color(row["color"])})
    chart_types = []
    if not isinstance(payload["chartTypes"], list): raise ValueError("invalid Excel export")
    for item in payload["chartTypes"]:
        if not isinstance(item, dict) or set(item) != {"type", "planned", "actualByResource", "plannedByResource", "actualTotalsByResource"}: raise ValueError("invalid Excel export")
        actual_by_resource = item["actualByResource"]
        if not isinstance(actual_by_resource, dict): raise ValueError("invalid Excel export")
        chart_types.append({"type": _text(item["type"]), "planned": series(item["planned"]), "actualByResource": {_text(key): series(value) for key, value in actual_by_resource.items()}, "plannedByResource": item["plannedByResource"], "actualTotalsByResource": item["actualTotalsByResource"]})
    return {"title": _text(payload["title"]) or "Short Term Planner UG", "rangeLabel": _text(payload["rangeLabel"]), "actualProgressMode": payload["actualProgressMode"] is True, "days": normalized_days, "activities": activities, "aggregates": payload["aggregates"], "chartTypes": chart_types, "chartImages": _chart_images(payload["chartImages"]), "legend": payload["legend"]}


def build_excel_export(payload: object) -> bytes:
    data = validate_excel_export_payload(payload)
    try:
        import xlsxwriter
    except ImportError as error:
        raise RuntimeError("xlsxwriter unavailable") from error
    stream = BytesIO()
    with xlsxwriter.Workbook(stream, {"in_memory": True}) as book:
        plan, charts = book.add_worksheet("Plan STP"), book.add_worksheet("Gráficas")
        border = {"border": 1, "border_color": "#17345B"}
        title = book.add_format({**border, "bold": True, "font_size": 16, "font_color": "#17345B"})
        header = book.add_format({**border, "bold": True, "align": "center", "bg_color": "#DBE8F2"})
        label = book.add_format({**border, "bold": True})
        blank_day = book.add_format({**border, "align": "center"})
        plan.write(0, 0, data["title"], title); plan.write(1, 0, data["rangeLabel"])
        plan.write(3, 0, "Actividad", header)
        for col, day in enumerate(data["days"], 1): plan.write(3, col, day["label"], header)
        row_number = 4
        for activity in data["activities"]:
            plan.write(row_number, 0, f"{activity['id']} · {activity['name']}", label)
            fmt = book.add_format({**border, "align": "center", "bg_color": activity["color"], "num_format": "0.##"})
            for col, value in enumerate(activity["planned"], 1):
                if value is None:
                    plan.write_blank(row_number, col, None, blank_day)
                else:
                    plan.write_number(row_number, col, value, fmt)
            row_number += 1
        for aggregate in data["aggregates"]:
            if not isinstance(aggregate, dict): continue
            plan.write(row_number, 0, _text(aggregate.get("label")), label)
            values, states = aggregate.get("values", []), aggregate.get("states", [])
            for col, value in enumerate(values if isinstance(values, list) else [], 1):
                state = states[col - 1] if isinstance(states, list) and col - 1 < len(states) else ""
                color = RED if state == "under-progress" else GREEN if state == "met-progress" else "#EDF4F8"
                plan.write(row_number, col, _text(value, 40), book.add_format({**border, "align": "center", "bg_color": color, "bold": state != ""}))
            row_number += 1
        plan.set_column(0, 0, 32); plan.set_column(1, len(data["days"]), 13); plan.freeze_panes(4, 1)
        plan.write(row_number + 1, 0, "Leyenda", label)
        for offset, item in enumerate(data["legend"] if isinstance(data["legend"], list) else [], 1):
            if isinstance(item, dict): plan.write(row_number + 1, offset, _text(item.get("label")), book.add_format({"bg_color": _color(item.get("color")), **border}))
        charts.hide_gridlines(2)
        for index, image in enumerate(data["chartImages"]):
            chart_row = (index // 2) * 26
            chart_column = 0 if index % 2 == 0 else 14
            charts.insert_image(chart_row, chart_column, "stp-chart.png", {
                "image_data": BytesIO(image["png"]), "x_scale": .8, "y_scale": .8,
            })
    return stream.getvalue()
