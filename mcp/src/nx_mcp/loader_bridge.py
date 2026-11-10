# Publication copy modified by repository maintainer on 2026-11-10.
# Original authors and licenses: docs/ATTRIBUTION.md.
"""C# Loader backend for the MCP sidecar.

Talks to the resident NX_MCP_Loader (NXOpen C# plugin) over a named pipe.
The Loader owns the NX main-thread dispatch (SendMessage + WndProc), so this
sidecar never touches NXOpen directly — it only sends text commands and reads
single-line JSON responses.

Backend selection lives in ``server.create_server``: when the Loader answers
``nx_status`` it is preferred; otherwise the sidecar falls back to the
original Python bridge (``DescriptorBridgeClient``).

Only the 15 phase-1 commands plus the 7 phase-2 commands (open/close part,
list sketches/bodies/features, undo, release) are supported — i.e. all 22
certified tools. Anything else raises ``NXToolError`` with ``NX_UNSUPPORTED``.
"""

from __future__ import annotations

import asyncio
import base64
import ctypes
import ctypes.wintypes as wt
import json
import time
from typing import Any

from nx_mcp.certified import BridgeCaller
from nx_mcp.runtime import NXToolError, ObjectKind, ObjectRef

PIPE_NAME = r"\\.\pipe\nx_mcp_loader"
_LOADER_VERSION = "unknown"

# Loader command syntax is a plain space-separated line. Numbers are formatted
# with invariant culture; booleans become 1/0. The full grammar is defined by
# NX_MCP_Loader.cs (Execute / Dispatch).


def _fmt(value: Any) -> str:
    if isinstance(value, bool):
        return "1" if value else "0"
    if isinstance(value, float):
        # avoid exponent / locale surprises; keep enough precision
        return f"{value:.6f}".rstrip("0").rstrip(".")
    return str(value)


def _p2(params: dict[str, Any], key: str) -> tuple[float, float]:
    pt = params[key]
    if isinstance(pt, dict):
        return float(pt["x"]), float(pt["y"])
    return float(pt.x), float(pt.y)


def _command(method: str, params: dict[str, Any]) -> str | None:
    if method.startswith(("nx_assembly_", "nx_drafting_", "nx_image_")):
        # Base64 carries Unicode names and paths without token-splitting or
        # newline injection. None means an omitted optional native argument.
        if not all(char.islower() or char == "_" or char.isdigit() for char in method):
            raise NXToolError("NX_INVALID_ARGUMENT", "Invalid automation method name")
        payload = {key: value for key, value in params.items() if value is not None}
        try:
            encoded = base64.b64encode(json.dumps(
                payload, ensure_ascii=False, allow_nan=False, separators=(",", ":")
            ).encode("utf-8")).decode("ascii")
        except (ValueError, TypeError) as error:
            raise NXToolError("NX_INVALID_ARGUMENT", "Automation parameters must be finite JSON values") from error
        return method + " " + encoded
    if method == "nx_status":
        return "nx_status"
    if method == "nx_create_part":
        units = params.get("units", "mm")
        if units not in {"mm", "inch"}:
            raise NXToolError("NX_INVALID_ARGUMENT", "units must be mm or inch")
        return "nx_create_part " + params["path"] + " " + units
    if method == "nx_open_part":
        return "nx_open_part " + params["path"]
    if method == "nx_save_part":
        return "nx_save_part"
    if method == "nx_close_part":
        return "nx_close_part " + _fmt(params.get("save", True))
    if method == "nx_export_step":
        return "nx_export_step " + params["path"]
    if method in ("nx_list_sketches", "nx_list_bodies", "nx_list_features"):
        return method
    if method == "nx_list_edges":
        return "nx_list_edges " + params["body_id"]
    if method == "nx_list_faces":
        return "nx_list_faces " + params["body_id"]
    if method == "nx_undo":
        return "nx_undo"
    if method == "nx_release":
        return "nx_release"
    # ---- drafting (2D drawing) -------------------------------------------
    # Backed by the C# Loader commands added on top of CAD-Agent-Hub's proven
    # sequences; see loader/NX_MCP_Loader.cs (CreateDrawingSheet / CreateProjectedView).
    # Tokenised protocol: sheet name / view name must not contain spaces.
    if method == "nx_create_drawing_sheet":
        scale = params.get("scale_numerator", params.get("scale", 1.0))
        return "nx_create_drawing_sheet %s %s %s %s %s" % (
            params.get("name", "MCP_SHEET_1"),
            params.get("size", "A4"),
            params.get("projection", "first"),
            _fmt(scale),
            params.get("model_view", "Top"),
        )
    if method == "nx_create_projected_view":
        if "position" in params:
            x, y = _p2(params, "position")
        else:
            x, y = params.get("x"), params.get("y")
        parent = str(params.get("parent_view_id") or params.get("parent_view") or "")
        name = str(params.get("name") or params.get("view_name") or "")
        cmd = "nx_create_projected_view %s %s" % (_fmt(x), _fmt(y))
        # parent is positional; supply "0" (first view) when only a name was given
        if name and not parent:
            parent = "0"
        if parent:
            cmd += " " + parent
            if name:
                cmd += " " + name
        return cmd
    if method == "nx_create_note":
        # text goes last so it may contain spaces; '|' marks a line break
        return "nx_create_note %s %s %s" % (
            _fmt(params.get("pos_x", 0.0)),
            _fmt(params.get("pos_y", 0.0)),
            str(params.get("text", "")),
        )
    if method == "nx_create_diameter_dimension":
        return "nx_create_diameter_dimension %s %s %s %s %s %s %s %s" % (
            str(params.get("view_id", "")),
            str(params.get("body_id", "")),
            str(params.get("edge_index", 0)),
            _fmt(params.get("pos_x", 0.0)),
            _fmt(params.get("pos_y", 0.0)),
            _fmt(params.get("upper_tolerance", 0.1)),
            _fmt(params.get("lower_tolerance", -0.1)),
            str(params.get("dim_name", "MCP_DIAMETER")),
        )
    if method == "nx_create_radius_dimension":
        return "nx_create_radius_dimension %s %s %s %s %s %s" % (
            str(params.get("view_id", "")),
            str(params.get("body_id", "")),
            str(params.get("edge_index", 0)),
            _fmt(params.get("pos_x", 0.0)),
            _fmt(params.get("pos_y", 0.0)),
            str(params.get("dim_name", "MCP_RADIUS")),
        )
    if method == "nx_measure_distance":
        return "nx_measure_distance %s %s %s" % (
            str(params.get("body_id", "")),
            str(params.get("edge_index_1", params.get("first_edge_index", 0))),
            str(params.get("edge_index_2", params.get("second_edge_index", 0))),
        )
    if method == "nx_export_dwg":
        cmd = "nx_export_dwg " + str(params.get("path", ""))
        sheet = str(params.get("sheet_name", "") or "")
        if sheet:
            cmd += " " + sheet
        return cmd
    if method == "nx_create_sheet_border":
        return "nx_create_sheet_border %s" % (
            "1" if str(params.get("zones", "1")) in ("1", "true", "True") else "0"
        )
    if method == "nx_set_attribute":
        return "nx_set_attribute %s %s" % (
            str(params.get("name", "")),
            str(params.get("value", "")),
        )
    if method == "nx_list_attributes":
        return "nx_list_attributes"
    if method == "nx_create_title_block":
        return "nx_create_title_block %s %s" % (
            _fmt(params.get("pos_x", 0.0)),
            _fmt(params.get("pos_y", 0.0)),
        )
    if method == "nx_create_detail_view":
        return "nx_create_detail_view %s %s %s %s %s %s" % (
            str(params.get("parent_view_id", "")),
            _fmt(params.get("x", 0.0)),
            _fmt(params.get("y", 0.0)),
            _fmt(params.get("radius", 10.0)),
            _fmt(params.get("scale", 2.0)),
            str(params.get("view_name", "")),
        )
    if method == "nx_create_roughness":
        return "nx_create_roughness %s %s %s" % (
            _fmt(params.get("pos_x", 0.0)),
            _fmt(params.get("pos_y", 0.0)),
            str(params.get("value", "1.6")),
        )
    if method == "nx_create_centerline":
        return "nx_create_centerline"
    if method == "nx_export_pdf":
        cmd = "nx_export_pdf " + str(params.get("path", ""))
        sheet = str(params.get("sheet_name", "") or "")
        if sheet:
            cmd += " " + sheet
        return cmd
    if method == "nx_delete_annotation":
        selector = params.get("annotation_id", "")
        if not isinstance(selector, str) or not selector.strip() or any(char in selector for char in ("\x00", "\r", "\n")):
            raise NXToolError("NX_INVALID_ARGUMENT", "annotation_id must be nonempty single-line text")
        return "nx_delete_annotation b64:" + base64.b64encode(selector.encode("utf-8")).decode("ascii")
    if method in (
        "nx_list_drawing_sheets",
        "nx_list_drafting_views",
        "nx_list_dimensions",
        "nx_list_annotations",
    ):
        return method
    # ---- drafting: view update + dimension annotation ------------
    if method == "nx_update_views":
        return "nx_update_views"
    if method == "nx_create_dimension":
        if "position" in params:
            x, y = _p2(params, "position")
        else:
            x = params.get("pos_x")
            y = params.get("pos_y")
        return "nx_create_dimension %s %s %s %s %s %s %s %s %s %s" % (
            str(params.get("view_id", "")),
            str(params.get("body_id", "")),
            int(params["first_edge_index"]), int(params["second_edge_index"]),
            _fmt(x), _fmt(y),
            params.get("measurement", "horizontal"),
            _fmt(params.get("upper_tolerance", 0.1)),
            _fmt(params.get("lower_tolerance", -0.1)),
            params.get("dim_name", "MCP_DIMENSION"),
        )
    if method == "nx_create_sketch":
        command = "nx_create_sketch " + params.get("plane", "XY")
        name = params.get("name")
        if name:
            if not isinstance(name, str) or any(char in name for char in ("\x00", "\r", "\n")):
                raise NXToolError("NX_INVALID_ARGUMENT", "sketch name must be single-line text")
            command += " " + base64.b64encode(name.encode("utf-8")).decode("ascii")
        return command
    if method == "nx_sketch_line":
        x1, y1 = _p2(params, "start")
        x2, y2 = _p2(params, "end")
        return "nx_sketch_line %s %s %s %s %s" % (
            params["sketch_id"], _fmt(x1), _fmt(y1), _fmt(x2), _fmt(y2))
    if method == "nx_sketch_rectangle":
        x1, y1 = _p2(params, "corner1")
        x2, y2 = _p2(params, "corner2")
        # The public tool accepts opposite corners; the resident loader accepts
        # center X/Y and positive width/height.
        return "nx_sketch_rectangle %s %s %s %s %s" % (
            params["sketch_id"], _fmt((x1 + x2) / 2), _fmt((y1 + y2) / 2),
            _fmt(abs(x2 - x1)), _fmt(abs(y2 - y1)))
    if method == "nx_sketch_circle":
        cx, cy = _p2(params, "center")
        return "nx_sketch_circle %s %s %s %s" % (
            params["sketch_id"], _fmt(cx), _fmt(cy), _fmt(params["diameter"]))
    if method == "nx_sketch_arc":
        cx, cy = _p2(params, "center")
        return "nx_sketch_arc %s %s %s %s %s %s" % (
            params["sketch_id"], _fmt(cx), _fmt(cy), _fmt(params["radius"]),
            _fmt(params.get("start_angle", 0.0)), _fmt(params.get("end_angle", 90.0)))
    if method == "nx_finish_sketch":
        return "nx_finish_sketch " + params["sketch_id"]
    if method == "nx_extrude":
        parts = [
            "nx_extrude", params["sketch_id"], _fmt(params["distance"]),
            _fmt(params.get("reverse", False)), _fmt(params.get("start_offset", 0.0)),
            params.get("operation", "create"),
        ]
        if params.get("target_body_id"):
            parts.append(params["target_body_id"])
        return " ".join(parts)
    if method == "nx_hole":
        cx, cy = _p2(params, "center")
        return "nx_hole %s %s %s %s %s %s" % (
            params["body_id"], _fmt(cx), _fmt(cy), _fmt(params["diameter"]),
            _fmt(params["depth"]), _fmt(params.get("start_offset", 0.0)))
    if method == "nx_counterbore_hole":
        cx, cy = _p2(params, "center")
        return "nx_counterbore_hole %s %s %s %s %s %s %s %s" % (
            params["body_id"], _fmt(cx), _fmt(cy),
            _fmt(params["hole_diameter"]), _fmt(params["hole_depth"]),
            _fmt(params["counterbore_diameter"]), _fmt(params["counterbore_depth"]),
            _fmt(params.get("start_offset", 0.0)))
    if method == "nx_countersink_hole":
        cx, cy = _p2(params, "center")
        return "nx_countersink_hole %s %s %s %s %s %s %s %s" % (
            params["body_id"], _fmt(cx), _fmt(cy),
            _fmt(params["hole_diameter"]), _fmt(params["hole_depth"]),
            _fmt(params["countersink_diameter"]), _fmt(params["countersink_angle"]),
            _fmt(params.get("start_offset", 0.0)))
    if method == "nx_shell":
        inward = "0" if params.get("inward", True) is False else "1"
        return "nx_shell %s %s %s %s" % (
            params["body_id"], _fmt(params["thickness"]),
            int(params["remove_face_index"]), inward)
    if method == "nx_edge_blend":
        cmd = "nx_edge_blend %s %s" % (params["body_id"], _fmt(params["radius"]))
        idx = params.get("edge_indices")
        if idx:
            cmd += " " + ",".join(str(int(i)) for i in idx)
        return cmd
    if method == "nx_chamfer":
        cmd = "nx_chamfer %s %s" % (params["body_id"], _fmt(params["offset"]))
        idx = params.get("edge_indices")
        if idx:
            cmd += " " + ",".join(str(int(i)) for i in idx)
        return cmd
    if method == "nx_unite":
        ids = params["tool_body_ids"]
        if isinstance(ids, str):
            csv = ids
        else:
            csv = ",".join(str(i) for i in ids)
        return "nx_unite %s %s" % (params["target_body_id"], csv)
    if method == "nx_revolve":
        sx, sy = _p2(params, "axis_start")
        ex, ey = _p2(params, "axis_end")
        return "nx_revolve %s %s %s %s %s %s %s" % (
            params["sketch_id"], _fmt(sx), _fmt(sy), _fmt(ex), _fmt(ey),
            _fmt(params.get("angle", 360.0)), _fmt(params.get("reverse", False)))
    if method == "nx_mirror":
        return "nx_mirror %s %s %s" % (
            params["body_id"], params["plane"], _fmt(params.get("offset", 0.0)))
    if method == "nx_linear_pattern":
        return "nx_linear_pattern %s %s %s %s %s" % (
            params["body_id"], params["direction"], int(params["count"]),
            _fmt(params["spacing"]), _fmt(params.get("reverse", False)))
    if method == "nx_circular_pattern":
        c = params["center"]
        return "nx_circular_pattern %s %s %s %s %s %s %s %s" % (
            params["body_id"], params["axis"],
            _fmt(c["x"]), _fmt(c["y"]), _fmt(c["z"]),
            int(params["count"]), _fmt(params["angle"]),
            _fmt(params.get("reverse", False)))
    if method == "nx_fit_view":
        return "nx_fit_view"
    return None  # unsupported by the phase-1 Loader


def _pipe_call(cmd: str, timeout: float = 180.0, pipe: str = PIPE_NAME) -> dict[str, Any]:
    """Send one command over the named pipe and read the single-line JSON reply.

    Uses ctypes so no third-party dependency (pywin32) is required.
    """
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    GENERIC_READ = 0x80000000
    GENERIC_WRITE = 0x40000000
    OPEN_EXISTING = 3
    INVALID_HANDLE = wt.HANDLE(-1).value
    ERROR_PIPE_BUSY = 231
    ERROR_ACCESS_DENIED = 5
    ERROR_BROKEN_PIPE = 109
    # ctypes otherwise converts Python integers to c_int when passing a HANDLE
    # back to an untyped function, truncating valid 64-bit Windows handles.
    dword_pointer = ctypes.POINTER(wt.DWORD)
    kernel32.CreateFileW.argtypes = [
        wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p,
        wt.DWORD, wt.DWORD, wt.HANDLE,
    ]
    kernel32.CreateFileW.restype = wt.HANDLE
    kernel32.WaitNamedPipeW.argtypes = [wt.LPCWSTR, wt.DWORD]
    kernel32.WaitNamedPipeW.restype = wt.BOOL
    kernel32.WriteFile.argtypes = [
        wt.HANDLE, ctypes.c_void_p, wt.DWORD, dword_pointer, ctypes.c_void_p,
    ]
    kernel32.WriteFile.restype = wt.BOOL
    kernel32.ReadFile.argtypes = list(kernel32.WriteFile.argtypes)
    kernel32.ReadFile.restype = wt.BOOL
    kernel32.PeekNamedPipe.argtypes = [
        wt.HANDLE, ctypes.c_void_p, wt.DWORD, dword_pointer,
        dword_pointer, dword_pointer,
    ]
    kernel32.PeekNamedPipe.restype = wt.BOOL
    kernel32.CloseHandle.argtypes = [wt.HANDLE]
    kernel32.CloseHandle.restype = wt.BOOL

    handle = None
    deadline = time.monotonic() + max(0.01, timeout)
    connect_deadline = min(deadline, time.monotonic() + 9.0)
    last_error = 0
    while time.monotonic() < connect_deadline:
        handle = kernel32.CreateFileW(
            pipe,
            GENERIC_READ | GENERIC_WRITE,
            0, None, OPEN_EXISTING, 0, None,
        )
        if handle and handle != INVALID_HANDLE:
            break
        err = ctypes.get_last_error()
        last_error = err
        if err == ERROR_ACCESS_DENIED:
            raise NXToolError(
                "NX_LOADER_ACCESS_DENIED",
                "Access to the NX Loader pipe was denied (Win32 error 5). "
                "Run NX and the client as the same ordinary Windows user; "
                "check the user-specific pipe ACL rather than granting Everyone access.",
            )
        if err == ERROR_PIPE_BUSY:
            kernel32.WaitNamedPipeW(pipe, min(500, max(1, int((connect_deadline - time.monotonic()) * 1000))))
        else:
            # server briefly re-creates the single-instance pipe between
            # connections; retry instead of failing immediately
            time.sleep(0.3)
        handle = None
    if not handle or handle == INVALID_HANDLE:
        raise NXToolError(
            "NX_LOADER_UNAVAILABLE",
            "NX_MCP_Loader pipe is not available (Win32 error %d); "
            "start NX with the plugin installed." % last_error,
        )
    try:
        payload = (cmd + "\n").encode("utf-8")
        payload_buffer = ctypes.create_string_buffer(payload)
        written = wt.DWORD(0)
        ok = bool(kernel32.WriteFile(
            handle, payload_buffer, len(payload), ctypes.byref(written), None))
        # Never resend a partly-written modeling command: that could duplicate
        # a mutation. A fresh command must be a deliberate caller decision.
        if not ok or written.value != len(payload):
            raise NXToolError(
                "NX_LOADER_WRITE",
                "failed to write loader command (err=%d)" % ctypes.get_last_error(),
            )

        buffer = bytearray()
        chunk = ctypes.create_string_buffer(65536)
        n = wt.DWORD(0)
        while True:
            if time.monotonic() >= deadline:
                raise NXToolError("NX_LOADER_TIMEOUT", "Timed out waiting for the NX Loader response")
            available = wt.DWORD(0)
            if not kernel32.PeekNamedPipe(handle, None, 0, None, ctypes.byref(available), None):
                err = ctypes.get_last_error()
                if err == ERROR_BROKEN_PIPE:
                    break
                raise NXToolError("NX_LOADER_READ", "Failed to inspect Loader response (Win32 error %d)" % err)
            if not available.value:
                time.sleep(0.02)
                continue
            if not kernel32.ReadFile(handle, chunk, min(len(chunk), available.value), ctypes.byref(n), None):
                err = ctypes.get_last_error()
                if err != ERROR_BROKEN_PIPE:
                    raise NXToolError("NX_LOADER_READ", "Failed to read Loader response (Win32 error %d)" % err)
                break
            if n.value == 0:
                continue
            buffer.extend(chunk.raw[: n.value])
            if b"\n" in buffer:
                break
    finally:
        kernel32.CloseHandle(handle)

    line = bytes(buffer).decode("utf-8", errors="replace").strip()
    if not line:
        raise NXToolError("NX_LOADER_EMPTY", "loader returned an empty response")
    try:
        return json.loads(line)
    except ValueError:
        raise NXToolError("NX_LOADER_BAD_JSON", "loader returned invalid JSON: " + line[:200])


def _ref(kind: ObjectKind, obj_id: str, part_id: str = "", name: str = "") -> ObjectRef:
    return ObjectRef(id=obj_id, kind=kind, name=name, part_id=part_id)


def _adapt(method: str, params: dict[str, Any], resp: dict[str, Any]) -> dict[str, Any]:
    if method.startswith(("nx_assembly_", "nx_drafting_", "nx_image_")):
        # Keep native IDs, geometry, status, and hierarchy. Legacy OperationResult
        # has a deliberately smaller contract and must not trim these additions.
        result = {key: value for key, value in resp.items() if key != "ok"}
        result.setdefault("status", "success")
        return result
    part_id = resp.get("part", params.get("path", ""))
    msg = resp.get("result") or resp.get("message") or "ok"
    if method == "nx_status":
        active = None
        if resp.get("part"):
            active = _ref("part", resp["part"], resp["part"])
        return {
            "status": "success",
            "connected": True,
            "nx_version": resp.get("nx", _LOADER_VERSION),
            "bridge_protocol": 1,
            "active_part": active,
        }
    if method == "nx_create_part":
        return {"status": "success", "part": _ref("part", resp.get("part", params["path"]), part_id), "message": msg}
    if method == "nx_open_part":
        return {"status": "success", "part": _ref("part", resp.get("part", params["path"]), part_id), "message": msg}
    if method == "nx_close_part":
        return {"status": "success", "message": msg}
    if method == "nx_export_step":
        return {"status": "success", "path": resp.get("step", params["path"]), "message": msg}
    if method == "nx_save_part":
        return {"status": "success", "message": "saved"}
    if method == "nx_fit_view":
        return {"status": "success", "message": "view fitted"}
    if method == "nx_undo":
        return {"status": "success", "message": msg}
    if method == "nx_release":
        # Loader stays resident: only task state is cleared.
        return {"status": "success", "message": msg}
    if method in ("nx_list_sketches", "nx_list_bodies", "nx_list_features"):
        kind: ObjectKind = {
            "nx_list_sketches": "sketch",
            "nx_list_bodies": "body",
            "nx_list_features": "feature",
        }[method]
        names = resp.get("objects") or []
        refs = resp.get("sketch_refs") if method == "nx_list_sketches" else None
        objects = ([_ref(kind, str(item["id"]), part_id, str(item.get("name", ""))) for item in refs]
                   if refs is not None else [_ref(kind, str(name), part_id) for name in names])
        return {"status": "success", "objects": objects, "message": msg}
    if method == "nx_list_edges":
        return {
            "status": "success",
            "edges": resp.get("edges") or [],
            "edge_count": resp.get("edge_count", 0),
            "message": msg,
            **{key: resp[key] for key in ("body_properties", "coordinate_system", "length_unit", "area_unit", "body_id") if key in resp},
        }
    if method == "nx_list_faces":
        return {
            "status": "success",
            "faces": resp.get("faces") or [],
            "face_count": resp.get("face_count", 0),
            "message": msg,
            **{key: resp[key] for key in ("body_properties", "coordinate_system", "length_unit", "area_unit", "body_id") if key in resp},
        }
    if method == "nx_extrude":
        body_id = resp.get("body_id", "")
        return {
            "status": "success",
            "feature": _ref("feature", body_id, part_id),
            "body": _ref("body", body_id, part_id),
            "message": msg,
        }
    if method == "nx_unite":
        body_id = resp.get("body_id", params.get("target_body_id", ""))
        return {
            "status": "success",
            "object": _ref("body", body_id, part_id),
            "message": msg,
        }
    if method == "nx_sketch_rectangle":
        obj_id = params.get("sketch_id", "")
        return {
            "status": "success",
            "objects": [_ref("curve", obj_id, part_id)],
            "message": msg,
        }
    if method == "nx_linear_pattern":
        ids = resp.get("body_ids") or []
        return {
            "status": "success",
            "objects": [_ref("body", str(i), part_id) for i in ids],
            "message": msg,
        }
    if method == "nx_circular_pattern":
        ids = resp.get("body_ids") or []
        return {
            "status": "success",
            "objects": [_ref("body", str(i), part_id) for i in ids],
            "message": msg,
        }
    if method in ("nx_create_sketch", "nx_sketch_line", "nx_sketch_circle",
                  "nx_sketch_arc", "nx_finish_sketch", "nx_hole",
                  "nx_counterbore_hole", "nx_countersink_hole",
                  "nx_shell",
                  "nx_edge_blend", "nx_chamfer", "nx_revolve", "nx_mirror"):
        obj_id = (
            resp.get("sketch_id")
            or resp.get("body_id")
            or params.get("sketch_id")
            or params.get("body_id")
            or ""
        )
        kind: ObjectKind = "body" if method in ("nx_hole", "nx_counterbore_hole", "nx_countersink_hole", "nx_shell", "nx_edge_blend", "nx_chamfer", "nx_revolve", "nx_mirror") else "sketch"
        return {"status": "success", "object": _ref(kind, obj_id, part_id, str(resp.get("sketch_name", "")) if method == "nx_create_sketch" else ""), "message": msg}
    # Drafting lists: ObjectRef.kind is a restricted literal (part/sketch/curve/
    # feature/body) and cannot carry "sheet"/"view", so surface the items inside
    # the message instead of returning them as objects.
    if method in (
        "nx_list_drawing_sheets",
        "nx_list_drafting_views",
        "nx_list_dimensions",
        "nx_list_annotations",
    ):
        items = resp.get("sheets")
        if items is None:
            items = resp.get("views")
        if items is None:
            items = resp.get("dimensions")
        if items is None:
            items = resp.get("annotations")
        items = items or []
        detail = ", ".join(str(i) for i in items) if items else "none"
        return {"status": "success", "message": "%s [%s]" % (msg, detail)}
    if method == "nx_update_views":
        return {"status": "success", "message": msg}
    if method == "nx_create_dimension":
        # OperationResult forbids extra fields. The NX annotation already exists
        # when this reply is adapted: never turn success into a retryable error.
        return {"status": "success", "message": msg}
    return {"status": "success", "message": msg}


class LoaderBridge:
    """BridgeCaller-compatible backend backed by the resident C# loader."""

    def __init__(self, pipe: str = PIPE_NAME) -> None:
        self.pipe = pipe
        self.last_ping_error: str | None = None

    def ping(self, timeout: float = 15.0) -> bool:
        self.last_ping_error = None
        try:
            resp = _pipe_call("nx_status", timeout=timeout, pipe=self.pipe)
            if resp.get("ok"):
                return True
            self.last_ping_error = str(resp.get("error") or "loader status returned ok=false")
            return False
        except Exception as exc:
            self.last_ping_error = str(exc)
            return False

    async def call(self, method: str, params: dict[str, Any]) -> dict[str, Any]:
        cmd = _command(method, params)
        if cmd is None:
            raise NXToolError(
                "NX_UNSUPPORTED",
                "C# Loader backend does not support %r yet; start the Python bridge for this tool." % method,
                retryable=False,
            )
        resp = await asyncio.to_thread(_pipe_call, cmd, 180.0, self.pipe)
        if not resp.get("ok"):
            if method.startswith(("nx_assembly_", "nx_drafting_", "nx_image_")):
                detail = dict(resp.get("details") or {})
                for key in ("command", "operation", "rollback", "rolled_back", "rollback_error"):
                    if key in resp:
                        detail[key] = resp[key]
                raise NXToolError(
                    str(resp.get("code") or "NX_AUTOMATION_ERROR"),
                    str(resp.get("error") or "NX12 automation command failed"),
                    nx_code=resp.get("nx_code", resp.get("nx_error_code")),
                    details=detail,
                    retryable=False,
                )
            raise NXToolError("NX_LOADER_ERROR", resp.get("error", "loader command failed"))
        return _adapt(method, params, resp)


def select_loader() -> BridgeCaller | None:
    """Return a LoaderBridge when the resident loader answers, else None."""
    try:
        bridge = LoaderBridge()
        if bridge.ping():
            return bridge
    except Exception:
        pass
    return None
