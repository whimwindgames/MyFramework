#!/usr/bin/env python3
"""hot-store 协议 v2 服务端测试。

直接运行：python3 test_hot_store.py
用临时目录与 HOT_STORE_CFG 隔离运行，不触碰 /etc/hot-store.json 与线上目录。
"""
import base64
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import time
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
TOOL = os.path.abspath(os.path.join(HERE, "..", "hot_store.py"))
CLIENT_CS = os.path.abspath(os.path.join(
    HERE, "..", "..", "..", "Packages", "com.whimwindgames.myframework",
    "Editor", "HotUpd", "Pub", "SshStore.cs"))


def enc(key):
    if key == "":
        return "."
    return base64.urlsafe_b64encode(key.encode("utf-8")).decode("ascii").rstrip("=")


def sha(data):
    return hashlib.sha256(data).hexdigest()


class HotStoreCase(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.mkdtemp(prefix="hot-store-test-")
        self.root = os.path.join(self.temp, "root")
        self.state = os.path.join(self.temp, "state")
        cfg = os.path.join(self.temp, "cfg.json")
        with open(cfg, "w", encoding="utf-8") as out:
            json.dump({"root": self.root, "state": self.state,
                       "leaseSeconds": 3600}, out)
        self.env = dict(os.environ, HOT_STORE_CFG=cfg)

    def tearDown(self):
        import shutil
        shutil.rmtree(self.temp, ignore_errors=True)

    def op(self, *args, payload=b"", check=True):
        proc = subprocess.run(
            [sys.executable, TOOL, *args], input=payload, env=self.env,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=30)
        if check and proc.returncode != 0:
            self.fail("hot-store失败:" + proc.stderr.decode("utf-8", "replace"))
        return proc

    def put(self, key, data, check=True):
        proc = self.op("put", enc(key), str(len(data)), sha(data),
                        payload=data, check=check)
        return proc

    def put_ok(self, key, data):
        proc = self.put(key, data)
        lines = proc.stdout.decode().splitlines()
        self.assertEqual("OK", lines[-1])
        return int(lines[0])

    def get(self, key, check=True):
        return self.op("get", enc(key), check=check)

    def list_keys(self, prefix):
        proc = self.op("list", enc(prefix))
        keys = []
        for line in proc.stdout.decode().splitlines():
            raw = base64.urlsafe_b64decode(line + "=" * (-len(line) % 4))
            keys.append(raw.decode("utf-8"))
        return keys


class ProtocolTest(HotStoreCase):
    def test_check_version(self):
        proc = self.op("check")
        self.assertEqual("hot-store/2", proc.stdout.decode().strip())

    def test_version_matches_unity_client(self):
        with open(TOOL, "r", encoding="utf-8") as source:
            server = re.search(r"^VERSION = (\d+)$", source.read(), re.M)
        with open(CLIENT_CS, "r", encoding="utf-8") as source:
            client = re.search(r"const int Version = (\d+);", source.read())
        self.assertIsNotNone(server, "服务端缺少VERSION常量")
        self.assertIsNotNone(client, "SshStore.cs缺少HotStoreProtocol.Version")
        self.assertEqual(server.group(1), client.group(1),
                         "服务端与Unity发布客户端协议版本不一致")

    def test_put_get_roundtrip(self):
        data = os.urandom(4096)
        offset = self.put_ok("test/releases/rel-1/files/a.bin", data)
        self.assertEqual(0, offset)
        proc = self.get("test/releases/rel-1/files/a.bin")
        self.assertEqual(data, proc.stdout)

    def test_put_same_content_is_idempotent(self):
        data = os.urandom(2048)
        self.put_ok("test/releases/rel-1/files/a.bin", data)
        proc = self.put("test/releases/rel-1/files/a.bin", data)
        lines = proc.stdout.decode().splitlines()
        self.assertEqual([str(len(data)), "OK"], lines)

    def test_put_immutable_rejects_different_content(self):
        self.put_ok("test/releases/rel-1/files/a.bin", os.urandom(1024))
        proc = self.put("test/releases/rel-1/files/a.bin", os.urandom(1024),
                        check=False)
        self.assertNotEqual(0, proc.returncode)
        self.assertIn("put_immutable", proc.stderr.decode())

    def test_mutable_latest_can_overwrite(self):
        key = "test/latest/Android/base-1.json"
        self.put_ok(key, b'{"seq":1}')
        self.put_ok(key, b'{"seq":2}')
        self.assertEqual(b'{"seq":2}', self.get(key).stdout)

    def test_resume_after_partial_upload(self):
        data = os.urandom(64 * 1024)
        half = len(data) // 2
        proc = self.op("put", enc("test/releases/rel-1/files/big.bin"),
                       str(len(data)), sha(data), payload=data[:half],
                       check=False)
        self.assertNotEqual(0, proc.returncode)
        self.assertIn("put_incomplete", proc.stderr.decode())
        # 真实客户端（SshStore）会按服务端回报的offset跳过已传部分。
        proc = self.op("put", enc("test/releases/rel-1/files/big.bin"),
                       str(len(data)), sha(data), payload=data[half:])
        lines = proc.stdout.decode().splitlines()
        self.assertEqual(str(half), lines[0], "断点续传应从半截位置继续")
        self.assertEqual("OK", lines[-1])
        self.assertEqual(data, self.get("test/releases/rel-1/files/big.bin").stdout)

    def test_sha_mismatch_rejected(self):
        data = os.urandom(1024)
        proc = self.op("put", enc("test/releases/rel-1/files/a.bin"),
                        str(len(data)), sha(b"wrong"), payload=data, check=False)
        self.assertNotEqual(0, proc.returncode)
        self.assertIn("put_sha256", proc.stderr.decode())
        target = os.path.join(self.root, "test/releases/rel-1/files/a.bin")
        self.assertFalse(os.path.exists(target))

    def test_bad_keys_rejected(self):
        for bad in ("../evil", "/abs/path", "a//b", "a\\b"):
            proc = self.put(bad, b"data", check=False)
            self.assertNotEqual(0, proc.returncode, bad)

    def test_list_prefix(self):
        self.put_ok("test/releases/rel-1/files/a.bin", b"a")
        self.put_ok("test/releases/rel-1/files/sub/b.bin", b"b")
        self.put_ok("test/releases/rel-2/files/c.bin", b"c")
        keys = self.list_keys("test/releases/rel-1/")
        self.assertEqual(["test/releases/rel-1/files/a.bin",
                          "test/releases/rel-1/files/sub/b.bin"], keys)

    def test_get_missing_rejected(self):
        self.put_ok("test/releases/rel-1/files/exists.bin", b"a")
        proc = self.get("test/releases/rel-1/files/missing.bin", check=False)
        self.assertNotEqual(0, proc.returncode)
        self.assertIn("get_missing", proc.stderr.decode())


class LeaseTest(HotStoreCase):
    TOKEN_A = "a" * 32
    TOKEN_B = "b" * 32
    LOCK = "test/locks/Android/base-1.lock"

    def take(self, token, check=True):
        return self.op("take", enc(self.LOCK), token, check=check)

    def test_take_busy_keep_release(self):
        self.assertEqual("OK", self.take(self.TOKEN_A).stdout.decode().strip())
        proc = self.take(self.TOKEN_B, check=False)
        self.assertNotEqual(0, proc.returncode)
        self.assertIn("lease_busy", proc.stderr.decode())
        self.assertEqual("OK", self.op("keep", enc(self.LOCK),
                                        self.TOKEN_A).stdout.decode().strip())
        proc = self.op("keep", enc(self.LOCK), self.TOKEN_B, check=False)
        self.assertIn("lease_lost", proc.stderr.decode())
        self.assertEqual("OK", self.op("release", enc(self.LOCK),
                                        self.TOKEN_A).stdout.decode().strip())
        self.assertEqual("OK", self.take(self.TOKEN_B).stdout.decode().strip())

    def test_expired_lease_can_be_retaken(self):
        self.assertEqual("OK", self.take(self.TOKEN_A).stdout.decode().strip())
        path = os.path.join(self.state, "leases",
                            hashlib.sha256(self.LOCK.encode()).hexdigest() + ".json")
        with open(path, "r", encoding="utf-8") as source:
            lease = json.load(source)
        lease["updated"] = int(time.time()) - 7200
        with open(path, "w", encoding="utf-8") as out:
            json.dump(lease, out)
        self.assertEqual("OK", self.take(self.TOKEN_B).stdout.decode().strip())

    def test_bad_token_rejected(self):
        proc = self.op("take", enc(self.LOCK), "short", check=False)
        self.assertNotEqual(0, proc.returncode)
        self.assertIn("lease_token", proc.stderr.decode())


if __name__ == "__main__":
    unittest.main()
