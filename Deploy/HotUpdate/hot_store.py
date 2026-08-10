#!/usr/bin/env python3
import base64
import fcntl
import hashlib
import json
import os
import shutil
import sys
import time


# 默认读取服务器标准配置；测试可用 HOT_STORE_CFG 指向临时配置，不影响线上行为。
CFG_PATH = os.environ.get("HOT_STORE_CFG", "/etc/hot-store.json")
VERSION = 2
BUF_SIZE = 1024 * 1024


def fail(message):
    print(message, file=sys.stderr)
    raise SystemExit(1)


def load_cfg():
    try:
        with open(CFG_PATH, "r", encoding="utf-8") as source:
            cfg = json.load(source)
    except (OSError, ValueError) as error:
        fail("config_error:" + type(error).__name__)
    if set(cfg) != {"root", "state", "leaseSeconds"}:
        fail("config_fields")
    root = os.path.realpath(cfg["root"])
    state = os.path.realpath(cfg["state"])
    lease = cfg["leaseSeconds"]
    if not os.path.isabs(root) or not os.path.isabs(state) or root == state:
        fail("config_path")
    if not isinstance(lease, int) or lease < 3600 or lease > 86400:
        fail("config_lease")
    os.makedirs(root, mode=0o755, exist_ok=True)
    os.makedirs(state, mode=0o700, exist_ok=True)
    return root, state, lease


def decode(value, empty_ok=False):
    if value == "." and empty_ok:
        return ""
    try:
        raw = base64.urlsafe_b64decode(value + "=" * (-len(value) % 4))
        key = raw.decode("utf-8")
    except (ValueError, UnicodeError):
        fail("key_encoding")
    if not key and empty_ok:
        return ""
    if not key or key.startswith("/") or "\\" in key or "//" in key:
        fail("key_format")
    if len(raw) > 1024:
        fail("key_size")
    parts = key.split("/")
    for part in parts:
        if part in (".", "..") or any(ord(char) < 32 or ord(char) == 127 for char in part):
            fail("key_format")
    return key


def safe_path(root, key):
    path = os.path.abspath(os.path.join(root, *key.split("/")))
    if os.path.commonpath((root, path)) != root:
        fail("key_scope")
    return path


def check_parents(root, path, create=False):
    parent = os.path.dirname(path)
    if create:
        os.makedirs(parent, mode=0o755, exist_ok=True)
    current = root
    relative = os.path.relpath(parent, root)
    if relative == ".":
        return
    for part in relative.split(os.sep):
        current = os.path.join(current, part)
        if os.path.islink(current) or not os.path.isdir(current):
            fail("path_parent")


def sha_file(path):
    value = hashlib.sha256()
    with open(path, "rb") as source:
        while True:
            block = source.read(BUF_SIZE)
            if not block:
                break
            value.update(block)
    return value.hexdigest()


def atomic_json(path, value):
    temp = path + ".tmp-" + os.urandom(8).hex()
    try:
        with open(temp, "x", encoding="utf-8") as output:
            json.dump(value, output, separators=(",", ":"))
            output.flush()
            os.fsync(output.fileno())
        os.replace(temp, path)
    finally:
        try:
            os.remove(temp)
        except FileNotFoundError:
            pass


def object_id(key):
    return hashlib.sha256(key.encode("utf-8")).hexdigest()


def mutable_key(key):
    parts = key.split("/")
    return (
        len(parts) == 4
        and parts[0] in ("test", "prod")
        and parts[1] in ("latest", "previous")
        and parts[3].endswith(".json")
    )


def put(root, state, key, size_text, sha):
    try:
        size = int(size_text)
    except ValueError:
        fail("put_size")
    if size < 1 or len(sha) != 64 or any(char not in "0123456789abcdef" for char in sha):
        fail("put_meta")
    target = safe_path(root, key)
    check_parents(root, target, True)
    part_dir = os.path.join(state, "parts", object_id(key))
    os.makedirs(part_dir, mode=0o700, exist_ok=True)
    lock_path = os.path.join(part_dir, "lock")
    with open(lock_path, "a+b") as gate:
        fcntl.flock(gate, fcntl.LOCK_EX)
        if os.path.lexists(target):
            if os.path.islink(target) or not os.path.isfile(target):
                fail("put_target")
            if os.path.getsize(target) == size and sha_file(target) == sha:
                print(size, flush=True)
                print("OK", flush=True)
                return
            if not mutable_key(key):
                fail("put_immutable")
        meta_path = os.path.join(part_dir, "meta.json")
        part_path = os.path.join(part_dir, "data.part")
        meta = None
        try:
            with open(meta_path, "r", encoding="utf-8") as source:
                meta = json.load(source)
        except (FileNotFoundError, ValueError):
            pass
        expected = {"key": key, "size": size, "sha256": sha}
        if meta != expected:
            try:
                os.remove(part_path)
            except FileNotFoundError:
                pass
            atomic_json(meta_path, expected)
        offset = os.path.getsize(part_path) if os.path.isfile(part_path) else 0
        if offset < 0 or offset > size:
            os.remove(part_path)
            offset = 0
        print(offset, flush=True)
        with open(part_path, "ab", buffering=0) as output:
            if output.tell() != offset:
                fail("put_offset")
            remaining = size - offset
            while remaining:
                block = sys.stdin.buffer.read(min(BUF_SIZE, remaining))
                if not block:
                    break
                output.write(block)
                remaining -= len(block)
            output.flush()
            os.fsync(output.fileno())
        if os.path.getsize(part_path) != size:
            fail("put_incomplete")
        if sha_file(part_path) != sha:
            os.remove(part_path)
            fail("put_sha256")
        os.chmod(part_path, 0o644)
        os.replace(part_path, target)
        try:
            os.remove(meta_path)
        except FileNotFoundError:
            pass
        print("OK", flush=True)


def get(root, key):
    path = safe_path(root, key)
    check_parents(root, path)
    if os.path.islink(path) or not os.path.isfile(path):
        fail("get_missing")
    with open(path, "rb") as source:
        shutil.copyfileobj(source, sys.stdout.buffer, BUF_SIZE)


def list_keys(root, prefix):
    keys = []
    if not prefix:
        start = root
    elif prefix.endswith("/"):
        start = safe_path(root, prefix[:-1])
    else:
        start = os.path.dirname(safe_path(root, prefix))
    if not os.path.exists(start):
        return
    if os.path.islink(start):
        fail("list_link")
    if os.path.isfile(start):
        start = os.path.dirname(start)
    for current, dirs, files in os.walk(start, followlinks=False):
        if any(os.path.islink(os.path.join(current, name)) for name in dirs):
            fail("list_link")
        for name in files:
            path = os.path.join(current, name)
            if os.path.islink(path):
                fail("list_link")
            key = os.path.relpath(path, root).replace(os.sep, "/")
            if key.startswith(prefix):
                keys.append(key)
    for key in sorted(keys):
        raw = base64.urlsafe_b64encode(key.encode("utf-8")).decode("ascii").rstrip("=")
        print(raw)


def valid_token(value):
    return (
        isinstance(value, str)
        and len(value) == 32
        and all(char in "0123456789abcdef" for char in value)
    )


def lease_op(state, lease_seconds, op, key, token):
    if not valid_token(token):
        fail("lease_token")
    lease_dir = os.path.join(state, "leases")
    os.makedirs(lease_dir, mode=0o700, exist_ok=True)
    path = os.path.join(lease_dir, object_id(key) + ".json")
    guard_path = os.path.join(lease_dir, ".guard")
    with open(guard_path, "a+b") as guard:
        fcntl.flock(guard, fcntl.LOCK_EX)
        current = None
        try:
            with open(path, "r", encoding="utf-8") as source:
                current = json.load(source)
        except (FileNotFoundError, ValueError):
            pass
        now = int(time.time())
        active = (
            isinstance(current, dict)
            and current.get("key") == key
            and valid_token(current.get("token", ""))
            and isinstance(current.get("updated"), int)
            and current["updated"] + lease_seconds > now
        )
        if op == "take":
            if active and current["token"] != token:
                fail("lease_busy:" + str(current["updated"] + lease_seconds))
            atomic_json(path, {"key": key, "token": token, "updated": now})
        elif op == "keep":
            if not active or current["token"] != token:
                fail("lease_lost")
            atomic_json(path, {"key": key, "token": token, "updated": now})
        elif op == "release":
            if isinstance(current, dict) and current.get("token") == token:
                try:
                    os.remove(path)
                except FileNotFoundError:
                    pass
        else:
            fail("lease_op")
    print("OK")


def main():
    root, state, lease_seconds = load_cfg()
    if len(sys.argv) < 2:
        fail("args")
    op = sys.argv[1]
    if op == "check" and len(sys.argv) == 2:
        print("hot-store/" + str(VERSION))
        return
    if op == "put" and len(sys.argv) == 5:
        put(root, state, decode(sys.argv[2]), sys.argv[3], sys.argv[4])
        return
    if op == "get" and len(sys.argv) == 3:
        get(root, decode(sys.argv[2]))
        return
    if op == "list" and len(sys.argv) == 3:
        list_keys(root, decode(sys.argv[2], True))
        return
    if op in ("take", "keep", "release") and len(sys.argv) == 4:
        lease_op(state, lease_seconds, op, decode(sys.argv[2]), sys.argv[3])
        return
    fail("args")


if __name__ == "__main__":
    main()
