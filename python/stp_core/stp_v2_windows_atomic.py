"""Windows-only atomic replacement with continuously write-protected handles."""

from __future__ import annotations

import ctypes
import hashlib
import os
import threading
import uuid
from ctypes import wintypes
from dataclasses import dataclass
from pathlib import Path


_GENERIC_READ = 0x80000000
_GENERIC_WRITE = 0x40000000
_DELETE = 0x00010000
_FILE_READ_ATTRIBUTES = 0x0080
_FILE_SHARE_READ = 0x00000001
_FILE_SHARE_WRITE = 0x00000002
_FILE_SHARE_DELETE = 0x00000004
_CREATE_NEW = 1
_OPEN_EXISTING = 3
_FILE_ATTRIBUTE_DIRECTORY = 0x00000010
_FILE_ATTRIBUTE_NORMAL = 0x00000080
_FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400
_FILE_FLAG_WRITE_THROUGH = 0x80000000
_FILE_FLAG_BACKUP_SEMANTICS = 0x02000000
_FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000
_FILE_RENAME_INFO_EX = 22
_FILE_RENAME_REPLACE_IF_EXISTS = 0x00000001
_FILE_RENAME_POSIX_SEMANTICS = 0x00000002
_FILE_BEGIN = 0
_ERROR_FILE_NOT_FOUND = 2
_ERROR_PATH_NOT_FOUND = 3
_ERROR_FILE_EXISTS = 80
_ERROR_ALREADY_EXISTS = 183
_INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value


class _ByHandleFileInformation(ctypes.Structure):
    _fields_ = (
        ("dwFileAttributes", wintypes.DWORD),
        ("ftCreationTime", wintypes.FILETIME),
        ("ftLastAccessTime", wintypes.FILETIME),
        ("ftLastWriteTime", wintypes.FILETIME),
        ("dwVolumeSerialNumber", wintypes.DWORD),
        ("nFileSizeHigh", wintypes.DWORD),
        ("nFileSizeLow", wintypes.DWORD),
        ("nNumberOfLinks", wintypes.DWORD),
        ("nFileIndexHigh", wintypes.DWORD),
        ("nFileIndexLow", wintypes.DWORD),
    )


class _FileRenameInfo(ctypes.Structure):
    _fields_ = (
        ("Flags", wintypes.DWORD),
        ("RootDirectory", wintypes.HANDLE),
        ("FileNameLength", wintypes.DWORD),
        ("FileName", wintypes.WCHAR * 1),
    )


@dataclass(frozen=True)
class _FileIdentity:
    volume: int
    index: int
    attributes: int


@dataclass(frozen=True)
class AtomicCommitResult:
    path: Path
    sha256: str


class AtomicCommitError(OSError):
    """A sanitized atomic-commit failure with a proved final state."""


class AtomicCommitIndeterminate(AtomicCommitError):
    """A commit failure whose rollback could not be proved."""

    def __init__(self, message: str, recovery_path: Path | None) -> None:
        super().__init__(message)
        self.recovery_path = recovery_path


@dataclass
class _DirectoryGuard:
    path: Path
    handle: int
    identity: _FileIdentity
    ancestors: tuple[tuple[int, _FileIdentity], ...]


_TARGET_LOCKS: dict[tuple[int, int, str], threading.Lock] = {}
_TARGET_LOCKS_GUARD = threading.Lock()


def _is_windows() -> bool:
    return os.name == "nt"


if os.name == "nt":
    _kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

    _CreateFileW = _kernel32.CreateFileW
    _CreateFileW.argtypes = (
        wintypes.LPCWSTR,
        wintypes.DWORD,
        wintypes.DWORD,
        wintypes.LPVOID,
        wintypes.DWORD,
        wintypes.DWORD,
        wintypes.HANDLE,
    )
    _CreateFileW.restype = wintypes.HANDLE

    _ReadFile = _kernel32.ReadFile
    _ReadFile.argtypes = (
        wintypes.HANDLE,
        wintypes.LPVOID,
        wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD),
        wintypes.LPVOID,
    )
    _ReadFile.restype = wintypes.BOOL

    _WriteFile = _kernel32.WriteFile
    _WriteFile.argtypes = (
        wintypes.HANDLE,
        wintypes.LPCVOID,
        wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD),
        wintypes.LPVOID,
    )
    _WriteFile.restype = wintypes.BOOL

    _SetFilePointerEx = _kernel32.SetFilePointerEx
    _SetFilePointerEx.argtypes = (
        wintypes.HANDLE,
        ctypes.c_longlong,
        ctypes.POINTER(ctypes.c_longlong),
        wintypes.DWORD,
    )
    _SetFilePointerEx.restype = wintypes.BOOL

    _FlushFileBuffers = _kernel32.FlushFileBuffers
    _FlushFileBuffers.argtypes = (wintypes.HANDLE,)
    _FlushFileBuffers.restype = wintypes.BOOL

    _GetFileInformationByHandle = _kernel32.GetFileInformationByHandle
    _GetFileInformationByHandle.argtypes = (
        wintypes.HANDLE,
        ctypes.POINTER(_ByHandleFileInformation),
    )
    _GetFileInformationByHandle.restype = wintypes.BOOL

    _SetFileInformationByHandle = _kernel32.SetFileInformationByHandle
    _SetFileInformationByHandle.argtypes = (
        wintypes.HANDLE,
        ctypes.c_int,
        wintypes.LPVOID,
        wintypes.DWORD,
    )
    _SetFileInformationByHandle.restype = wintypes.BOOL

    _DeleteFileW = _kernel32.DeleteFileW
    _DeleteFileW.argtypes = (wintypes.LPCWSTR,)
    _DeleteFileW.restype = wintypes.BOOL

    _CloseHandle = _kernel32.CloseHandle
    _CloseHandle.argtypes = (wintypes.HANDLE,)
    _CloseHandle.restype = wintypes.BOOL


def _raise_win32(message: str) -> None:
    raise AtomicCommitError(ctypes.get_last_error(), message)


def _open_handle(
    path: Path,
    *,
    desired_access: int,
    share_mode: int,
    creation: int,
    flags: int,
) -> int:
    handle = _CreateFileW(str(path), desired_access, share_mode, None, creation, flags, None)
    if handle in (None, _INVALID_HANDLE_VALUE):
        _raise_win32("could not open protected filesystem object")
    return int(handle)


def _close_handle(handle: int) -> None:
    if not _CloseHandle(handle):
        _raise_win32("could not close protected filesystem object")


def _identity_from_handle(handle: int) -> _FileIdentity:
    information = _ByHandleFileInformation()
    if not _GetFileInformationByHandle(handle, ctypes.byref(information)):
        _raise_win32("could not prove filesystem identity")
    return _FileIdentity(
        int(information.dwVolumeSerialNumber),
        (int(information.nFileIndexHigh) << 32) | int(information.nFileIndexLow),
        int(information.dwFileAttributes),
    )


def _same_file(left: _FileIdentity, right: _FileIdentity) -> bool:
    return left.volume == right.volume and left.index == right.index


def _visible_identity(
    path: Path,
    *,
    absent_ok: bool = False,
    require_directory: bool = False,
) -> _FileIdentity | None:
    flags = _FILE_FLAG_OPEN_REPARSE_POINT
    if require_directory:
        flags |= _FILE_FLAG_BACKUP_SEMANTICS
    handle = _CreateFileW(
        str(path),
        _FILE_READ_ATTRIBUTES,
        _FILE_SHARE_READ | _FILE_SHARE_WRITE | _FILE_SHARE_DELETE,
        None,
        _OPEN_EXISTING,
        flags,
        None,
    )
    if handle in (None, _INVALID_HANDLE_VALUE):
        code = ctypes.get_last_error()
        if absent_ok and code in (_ERROR_FILE_NOT_FOUND, _ERROR_PATH_NOT_FOUND):
            return None
        raise AtomicCommitError(code, "could not prove visible filesystem identity")
    try:
        identity = _identity_from_handle(int(handle))
    finally:
        _close_handle(int(handle))
    if identity.attributes & _FILE_ATTRIBUTE_REPARSE_POINT:
        raise AtomicCommitError("reparse points are not permitted")
    if require_directory:
        if not identity.attributes & _FILE_ATTRIBUTE_DIRECTORY:
            raise AtomicCommitError("protected parent must be a directory")
    elif identity.attributes & _FILE_ATTRIBUTE_DIRECTORY:
        raise AtomicCommitError("protected target must be a regular file")
    return identity


def _open_directory_guard(path: Path) -> _DirectoryGuard:
    proofs: list[tuple[int, _FileIdentity]] = []
    try:
        for component in (*reversed(path.parents), path):
            handle = _open_handle(
                component,
                desired_access=_FILE_READ_ATTRIBUTES,
                share_mode=_FILE_SHARE_READ | _FILE_SHARE_WRITE,
                creation=_OPEN_EXISTING,
                flags=_FILE_FLAG_BACKUP_SEMANTICS | _FILE_FLAG_OPEN_REPARSE_POINT,
            )
            try:
                identity = _identity_from_handle(handle)
                if (
                    not identity.attributes & _FILE_ATTRIBUTE_DIRECTORY
                    or identity.attributes & _FILE_ATTRIBUTE_REPARSE_POINT
                ):
                    raise AtomicCommitError("protected path must contain only non-reparse directories")
            except BaseException:
                _close_handle(handle)
                raise
            proofs.append((handle, identity))
        handle, identity = proofs[-1]
        return _DirectoryGuard(path, handle, identity, tuple(proofs[:-1]))
    except BaseException:
        for handle, _ in reversed(proofs):
            try:
                _close_handle(handle)
            except AtomicCommitError:
                pass
        raise


def _probe_directory_identity(path: Path) -> _FileIdentity:
    identity: _FileIdentity | None = None
    for component in (*reversed(path.parents), path):
        identity = _visible_identity(component, require_directory=True)
    if identity is None:
        raise AtomicCommitError("protected parent identity unavailable")
    return identity


def _verify_directory_guard(guard: _DirectoryGuard) -> None:
    for handle, expected in guard.ancestors:
        held_ancestor = _identity_from_handle(handle)
        if (
            not _same_file(held_ancestor, expected)
            or not held_ancestor.attributes & _FILE_ATTRIBUTE_DIRECTORY
            or held_ancestor.attributes & _FILE_ATTRIBUTE_REPARSE_POINT
        ):
            raise AtomicCommitError("protected ancestor identity changed")
    held = _identity_from_handle(guard.handle)
    visible = _visible_identity(guard.path, require_directory=True)
    if visible is None or not _same_file(held, guard.identity) or not _same_file(visible, guard.identity):
        raise AtomicCommitError("protected parent identity changed")
    if held.attributes & _FILE_ATTRIBUTE_REPARSE_POINT:
        raise AtomicCommitError("protected parent became a reparse point")


def _close_directory_guard(guard: _DirectoryGuard) -> None:
    failures = False
    for handle in (guard.handle, *(item[0] for item in reversed(guard.ancestors))):
        try:
            _close_handle(handle)
        except AtomicCommitError:
            failures = True
    if failures:
        raise AtomicCommitError("could not close protected directory proof")


def _new_sibling(parent: Path, target_name: str, suffix: str) -> Path:
    return parent / f".{target_name}.{uuid.uuid4().hex}{suffix}"


def _create_protected_sibling(parent: Path, target_name: str, suffix: str) -> tuple[Path, int]:
    for _ in range(128):
        path = _new_sibling(parent, target_name, suffix)
        handle = _CreateFileW(
            str(path),
            _GENERIC_READ | _GENERIC_WRITE | _DELETE,
            _FILE_SHARE_READ | _FILE_SHARE_DELETE,
            None,
            _CREATE_NEW,
            _FILE_ATTRIBUTE_NORMAL | _FILE_FLAG_WRITE_THROUGH,
            None,
        )
        if handle not in (None, _INVALID_HANDLE_VALUE):
            return path, int(handle)
        if ctypes.get_last_error() not in (_ERROR_FILE_EXISTS, _ERROR_ALREADY_EXISTS):
            _raise_win32("could not create protected temporary file")
    raise AtomicCommitError("could not allocate protected temporary file")


def _create_protected_temp(parent: Path, target_name: str) -> tuple[Path, int]:
    return _create_protected_sibling(parent, target_name, ".tmp")


def _delete_file(path: Path, *, absent_ok: bool = False) -> None:
    if _DeleteFileW(str(path)):
        return
    code = ctypes.get_last_error()
    if absent_ok and code in (_ERROR_FILE_NOT_FOUND, _ERROR_PATH_NOT_FOUND):
        return
    raise AtomicCommitError(code, "could not remove protected filesystem object")


def _seek_start(handle: int) -> None:
    if not _SetFilePointerEx(handle, 0, None, _FILE_BEGIN):
        _raise_win32("could not seek protected file handle")


def _write_all(handle: int, payload: bytes) -> None:
    offset = 0
    while offset < len(payload):
        chunk = payload[offset : offset + 1_048_576]
        written = wintypes.DWORD()
        buffer = ctypes.create_string_buffer(chunk)
        if not _WriteFile(handle, buffer, len(chunk), ctypes.byref(written), None):
            _raise_win32("could not write protected payload")
        if written.value <= 0:
            raise AtomicCommitError("protected payload write made no progress")
        offset += int(written.value)


def _read_all_handle(handle: int) -> bytes:
    _seek_start(handle)
    chunks: list[bytes] = []
    while True:
        buffer = ctypes.create_string_buffer(65_536)
        count = wintypes.DWORD()
        if not _ReadFile(handle, buffer, len(buffer), ctypes.byref(count), None):
            _raise_win32("could not read protected file handle")
        if count.value == 0:
            break
        chunks.append(buffer.raw[: count.value])
    _seek_start(handle)
    return b"".join(chunks)


def _flush_handle(handle: int) -> None:
    if not _FlushFileBuffers(handle):
        _raise_win32("could not make protected file durable")


def _digest_handle(handle: int) -> str:
    return hashlib.sha256(_read_all_handle(handle)).hexdigest()


def _open_existing_target(path: Path) -> tuple[int, _FileIdentity] | None:
    handle = _CreateFileW(
        str(path),
        _GENERIC_READ | _DELETE,
        _FILE_SHARE_READ | _FILE_SHARE_DELETE,
        None,
        _OPEN_EXISTING,
        _FILE_FLAG_OPEN_REPARSE_POINT,
        None,
    )
    if handle in (None, _INVALID_HANDLE_VALUE):
        code = ctypes.get_last_error()
        if code in (_ERROR_FILE_NOT_FOUND, _ERROR_PATH_NOT_FOUND):
            return None
        raise AtomicCommitError(code, "could not protect existing target")
    integer_handle = int(handle)
    try:
        identity = _identity_from_handle(integer_handle)
        if identity.attributes & (_FILE_ATTRIBUTE_DIRECTORY | _FILE_ATTRIBUTE_REPARSE_POINT):
            raise AtomicCommitError("protected target must be a non-reparse regular file")
        return integer_handle, identity
    except BaseException:
        _close_handle(integer_handle)
        raise


def _rename_handle(
    handle: int,
    destination: Path,
    *,
    expected_parent: Path,
    replace: bool,
) -> None:
    destination = Path(destination)
    expected_parent = Path(expected_parent)
    if (
        not destination.is_absolute()
        or not expected_parent.is_absolute()
        or destination.parent != expected_parent
        or destination != expected_parent / destination.name
    ):
        raise AtomicCommitError("handle rename requires an absolute exact-child destination")
    encoded = str(destination).encode("utf-16-le")
    size = _FileRenameInfo.FileName.offset + len(encoded) + ctypes.sizeof(wintypes.WCHAR)
    buffer = ctypes.create_string_buffer(size)
    information = _FileRenameInfo.from_buffer(buffer)
    information.Flags = (
        _FILE_RENAME_REPLACE_IF_EXISTS | _FILE_RENAME_POSIX_SEMANTICS
        if replace
        else 0
    )
    information.RootDirectory = None
    information.FileNameLength = len(encoded)
    ctypes.memmove(
        ctypes.addressof(buffer) + _FileRenameInfo.FileName.offset,
        encoded,
        len(encoded),
    )
    if not _SetFileInformationByHandle(handle, _FILE_RENAME_INFO_EX, buffer, size):
        _raise_win32("required handle-rename capability unavailable")


def _target_lock(parent_identity: _FileIdentity, target_name: str) -> threading.Lock:
    key = (
        parent_identity.volume,
        parent_identity.index,
        os.path.normcase(target_name),
    )
    with _TARGET_LOCKS_GUARD:
        return _TARGET_LOCKS.setdefault(key, threading.Lock())


def _verified_recovery_path(
    backup: Path,
    backup_handle: int,
    old_digest: str,
) -> Path | None:
    try:
        held = _identity_from_handle(backup_handle)
        visible = _visible_identity(backup)
        if (
            visible is not None
            and _same_file(held, visible)
            and _digest_handle(backup_handle) == old_digest
        ):
            return backup
    except AtomicCommitError:
        pass
    return None


def _rollback_existing_target(
    final: Path,
    backup: Path,
    backup_handle: int,
    old_handle: int,
    old_identity: _FileIdentity,
    old_digest: str,
    guard: _DirectoryGuard,
) -> None:
    try:
        backup_identity = _identity_from_handle(backup_handle)
        visible_backup = _visible_identity(backup)
        if (
            visible_backup is None
            or not _same_file(backup_identity, visible_backup)
            or _digest_handle(backup_handle) != old_digest
        ):
            raise AtomicCommitError("recovery backup verification failed")
        _verify_directory_guard(guard)
        _rename_handle(
            backup_handle,
            final,
            expected_parent=guard.path,
            replace=True,
        )
        _flush_handle(backup_handle)
        if _digest_handle(backup_handle) != old_digest:
            raise AtomicCommitError("restored target digest mismatch")
        visible_restored = _visible_identity(final)
        if visible_restored is None or not _same_file(backup_identity, visible_restored):
            raise AtomicCommitError("restored target identity mismatch")
        if _visible_identity(backup, absent_ok=True) is not None:
            raise AtomicCommitError("recovery backup name remained after restore")
        _verify_directory_guard(guard)
        return
    except AtomicCommitError:
        recovery = _verified_recovery_path(backup, backup_handle, old_digest)
        if recovery is None:
            try:
                held_old = _identity_from_handle(old_handle)
                if not _same_file(held_old, old_identity) or _digest_handle(old_handle) != old_digest:
                    raise AtomicCommitError("original target recovery verification failed")
                _rename_handle(
                    old_handle,
                    backup,
                    expected_parent=guard.path,
                    replace=False,
                )
                _flush_handle(old_handle)
                visible_old = _visible_identity(backup)
                if (
                    visible_old is None
                    or not _same_file(old_identity, visible_old)
                    or _digest_handle(old_handle) != old_digest
                ):
                    raise AtomicCommitError("original target recovery preservation failed")
                recovery = backup
            except AtomicCommitError:
                recovery = None
        raise AtomicCommitIndeterminate(
            "protected rollback could not be verified",
            recovery,
        ) from None


def _rollback_absent_target(
    final: Path,
    committed_handle: int,
    committed_identity: _FileIdentity,
    expected_digest: str,
    guard: _DirectoryGuard,
) -> None:
    rollback_path = _new_sibling(final.parent, final.name, ".restore.tmp")
    try:
        _verify_directory_guard(guard)
        _rename_handle(
            committed_handle,
            rollback_path,
            expected_parent=guard.path,
            replace=False,
        )
        if _visible_identity(final, absent_ok=True) is not None:
            raise AtomicCommitError("target name remained after rollback")
        visible_rollback = _visible_identity(rollback_path)
        if visible_rollback is None or not _same_file(committed_identity, visible_rollback):
            raise AtomicCommitError("rolled-back identity mismatch")
        _flush_handle(committed_handle)
        if _digest_handle(committed_handle) != expected_digest:
            raise AtomicCommitError("rolled-back identity digest mismatch")
        _delete_file(rollback_path)
        if _visible_identity(final, absent_ok=True) is not None:
            raise AtomicCommitError("target name reappeared after rollback")
        _verify_directory_guard(guard)
    except AtomicCommitError:
        raise AtomicCommitIndeterminate(
            "protected rollback could not be verified",
            None,
        ) from None


def atomic_replace_bytes_windows(
    target: Path,
    payload: bytes,
    *,
    expected_parent: Path,
) -> AtomicCommitResult:
    if not _is_windows():
        raise AtomicCommitError("protected atomic replacement requires Windows")
    if not isinstance(payload, bytes):
        raise AtomicCommitError("atomic payload must be bytes")

    parent = Path(os.path.abspath(os.fspath(expected_parent)))
    final = Path(os.path.abspath(os.fspath(target)))
    if final.parent != parent:
        raise AtomicCommitError("target must be an exact child of the protected parent")

    probed_parent = _probe_directory_identity(parent)
    with _target_lock(probed_parent, final.name):
        guard = _open_directory_guard(parent)
        try:
            _verify_directory_guard(guard)
            if not _same_file(probed_parent, guard.identity):
                raise AtomicCommitError("protected parent identity changed before commit")
            return _atomic_replace_bytes_windows_locked(final, payload, parent, guard)
        finally:
            try:
                _close_directory_guard(guard)
            except AtomicCommitError:
                pass


def _atomic_replace_bytes_windows_locked(
    final: Path,
    payload: bytes,
    parent: Path,
    guard: _DirectoryGuard,
) -> AtomicCommitResult:
    expected_digest = hashlib.sha256(payload).hexdigest()
    temporary: Path | None = None
    temporary_handle: int | None = None
    temporary_identity: _FileIdentity | None = None
    old_handle: int | None = None
    old_identity: _FileIdentity | None = None
    old_digest: str | None = None
    backup: Path | None = None
    backup_handle: int | None = None
    replaced = False
    try:
        _verify_directory_guard(guard)
        temporary, temporary_handle = _create_protected_temp(parent, final.name)
        _write_all(temporary_handle, payload)
        _flush_handle(temporary_handle)
        if _digest_handle(temporary_handle) != expected_digest:
            raise AtomicCommitError("protected temporary digest mismatch")
        _verify_directory_guard(guard)
        temporary_identity = _identity_from_handle(temporary_handle)
        visible_temporary = _visible_identity(temporary)
        if visible_temporary is None or not _same_file(temporary_identity, visible_temporary):
            raise AtomicCommitError("protected temporary identity changed")

        existing = _open_existing_target(final)
        if existing is not None:
            old_handle, old_identity = existing
            old_payload = _read_all_handle(old_handle)
            old_digest = hashlib.sha256(old_payload).hexdigest()
            backup, backup_handle = _create_protected_sibling(parent, final.name, ".bak")
            _write_all(backup_handle, old_payload)
            _flush_handle(backup_handle)
            if _digest_handle(backup_handle) != old_digest:
                raise AtomicCommitError("recovery backup digest mismatch")
            backup_identity = _identity_from_handle(backup_handle)
            visible_backup = _visible_identity(backup)
            visible_target = _visible_identity(final)
            held_old = _identity_from_handle(old_handle)
            if (
                visible_backup is None
                or visible_target is None
                or not _same_file(backup_identity, visible_backup)
                or not _same_file(old_identity, held_old)
                or not _same_file(old_identity, visible_target)
            ):
                raise AtomicCommitError("protected backup or target identity changed")
        elif _visible_identity(final, absent_ok=True) is not None:
            raise AtomicCommitError("protected target identity changed")

        _verify_directory_guard(guard)
        _rename_handle(
            temporary_handle,
            final,
            expected_parent=guard.path,
            replace=old_identity is not None,
        )
        replaced = True

        visible_committed = _visible_identity(final)
        if visible_committed is None or not _same_file(temporary_identity, visible_committed):
            raise AtomicCommitError("committed target identity mismatch")

        _flush_handle(temporary_handle)
        if _digest_handle(temporary_handle) != expected_digest:
            raise AtomicCommitError("committed target digest mismatch")
        visible_committed = _visible_identity(final)
        if visible_committed is None or not _same_file(temporary_identity, visible_committed):
            raise AtomicCommitError("committed target identity mismatch")
        _verify_directory_guard(guard)

        if backup is not None:
            backup_identity = _identity_from_handle(backup_handle)
            visible_backup = _visible_identity(backup)
            if visible_backup is None or not _same_file(backup_identity, visible_backup):
                raise AtomicCommitError("recovery backup identity changed")
            _delete_file(backup)
            backup = None
        return AtomicCommitResult(final, expected_digest)
    except (OSError, TypeError, ValueError) as error:
        if replaced:
            if temporary_handle is None or temporary_identity is None:
                raise AtomicCommitIndeterminate(
                    "protected rollback could not be verified",
                    backup,
                ) from None
            if old_identity is not None:
                if (
                    backup is None
                    or backup_handle is None
                    or old_handle is None
                    or old_digest is None
                ):
                    raise AtomicCommitIndeterminate(
                        "protected rollback could not be verified",
                        backup,
                    ) from None
                _rollback_existing_target(
                    final,
                    backup,
                    backup_handle,
                    old_handle,
                    old_identity,
                    old_digest,
                    guard,
                )
                backup = None
            else:
                _rollback_absent_target(
                    final,
                    temporary_handle,
                    temporary_identity,
                    expected_digest,
                    guard,
                )
            raise AtomicCommitError("protected atomic replacement failed") from None
        if isinstance(error, AtomicCommitError):
            raise
        raise AtomicCommitError("protected atomic replacement failed") from None
    finally:
        if temporary_handle is not None:
            try:
                _close_handle(temporary_handle)
            except AtomicCommitError:
                pass
        if old_handle is not None:
            try:
                _close_handle(old_handle)
            except AtomicCommitError:
                pass
        if backup_handle is not None:
            try:
                _close_handle(backup_handle)
            except AtomicCommitError:
                pass
        if temporary is not None and not replaced:
            try:
                _delete_file(temporary, absent_ok=True)
            except AtomicCommitError:
                pass
        if backup is not None and not replaced:
            try:
                _delete_file(backup, absent_ok=True)
            except AtomicCommitError:
                pass
