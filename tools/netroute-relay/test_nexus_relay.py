#!/usr/bin/env python3
# Testes do relay da rota de jogo (sem rede, sem root): python3 -m unittest tools/netroute-relay/test_nexus_relay.py
import base64
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import nexus_relay as n  # noqa: E402

SERVER_KEY = base64.b64encode(bytes(range(1, 33))).decode()
CLIENT_KEY = base64.b64encode(bytes(range(101, 133))).decode()
# Mesmo vetor em tests/Nexus.Tests/Modules/NetRouteTests.cs (o C# precisa ler exatamente isto).
VECTOR = ("nexus-relay://v1/AQECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8g_yKIUFeVIEcKQgAHCkIAASJTymwLNDUuNzkuMTAuMjANRm9ydGFsZXphIFZQUwlmb3J0YWxlemE")


class LinkTests(unittest.TestCase):
    def test_vector_matches_csharp(self):
        fp = n.key_fingerprint(CLIENT_KEY)
        self.assertEqual("ff22885057952047", fp)
        link = n.encode_link("Fortaleza VPS", "45.79.10.20", 51820, SERVER_KEY, fp, "10.66.0.7", "10.66.0.1", 8787, "fortaleza")
        self.assertEqual(VECTOR, link)
        self.assertEqual("rb01198f80d", n.relay_id(SERVER_KEY))

    def test_round_trip(self):
        link = n.encode_link("São Paulo", "relay.exemplo.com.br", 443, SERVER_KEY, "0123456789abcdef", "100.64.0.9", "100.64.0.1", 9000)
        d = n.decode_link(link)
        self.assertEqual("São Paulo", d["name"])
        self.assertEqual("relay.exemplo.com.br", d["host"])
        self.assertEqual(443, d["port"])
        self.assertEqual("100.64.0.9", d["client_ip"])
        self.assertIsNone(d["place"])

    def test_rejects_bad_fields(self):
        fp = n.key_fingerprint(CLIENT_KEY)
        with self.assertRaises(ValueError):
            n.encode_link("a[b]", "45.79.10.20", 1, SERVER_KEY, fp, "10.66.0.2", "10.66.0.1", 1)
        with self.assertRaises(ValueError):
            n.encode_link("ok", "host inválido", 1, SERVER_KEY, fp, "10.66.0.2", "10.66.0.1", 1)
        with self.assertRaises(ValueError):
            n.encode_link("ok", "45.79.10.20", 1, SERVER_KEY, fp, "10.66.0.2", "10.66.0.1", 1, "atlantida")
        with self.assertRaises(ValueError):
            n.encode_link("ok", "x" * 50 + ".com", 1, SERVER_KEY, fp, "10.66.0.2", "10.66.0.1", 1)  # 54 ok
            n.encode_link("ok", "x" * 61, 1, SERVER_KEY, fp, "10.66.0.2", "10.66.0.1", 1)
        with self.assertRaises(ValueError):
            n.decode_link(VECTOR[:-6])

    def test_worst_case_fits_260(self):
        link = n.encode_link("N" * 40, "a" * 56 + ".com", 65535, SERVER_KEY, "f" * 16, "10.255.255.254", "10.255.255.1", 65535, "rio-de-janeiro")
        self.assertLessEqual(len(link), 260)

    def test_key_validation(self):
        self.assertTrue(n.b64key_valid(SERVER_KEY))
        self.assertFalse(n.b64key_valid(SERVER_KEY[:-1]))
        self.assertFalse(n.b64key_valid("x" * 44))


class AllocationTests(unittest.TestCase):
    def test_skips_agent_and_used(self):
        self.assertEqual("10.66.0.2", n.allocate_ip("10.66.0.0/24", [], "10.66.0.1"))
        self.assertEqual("10.66.0.4", n.allocate_ip("10.66.0.0/24", ["10.66.0.2", "10.66.0.3"], "10.66.0.1"))

    def test_full_subnet(self):
        used = ["10.66.0.%d" % i for i in range(2, 15)]
        with self.assertRaises(RuntimeError):
            n.allocate_ip("10.66.0.0/28", used, "10.66.0.1")

    def test_wg_conf_contains_only_public_keys_of_peers(self):
        cfg = {"agent_ip": "10.66.0.1", "subnet": "10.66.0.0/24", "port": 51820, "server_private_key": "PRIV",
               "peers": [{"public_key": CLIENT_KEY, "ip": "10.66.0.2", "label": "PC"}]}
        text = n.render_wg_conf(cfg)
        self.assertIn("Address = 10.66.0.1/24", text)
        self.assertIn("AllowedIPs = 10.66.0.2/32", text)
        self.assertIn("PublicKey = " + CLIENT_KEY, text)


class AgentTests(unittest.TestCase):
    def test_parse_ping(self):
        out = ("PING 1.1.1.1 (1.1.1.1) 56(84) bytes of data.\n"
               "64 bytes from 1.1.1.1: icmp_seq=1 ttl=57 time=12.3 ms\n"
               "64 bytes from 1.1.1.1: icmp_seq=3 ttl=57 time=11.9 ms\n")
        self.assertEqual([12.3, None, 11.9], n.parse_ping(out, 3))

    def test_targets(self):
        self.assertIsNotNone(n.target_allowed("1.1.1.1"))
        self.assertIsNotNone(n.target_allowed("2606:4700:4700::1111"))
        for bad in ["10.0.0.1", "192.168.1.1", "169.254.169.254", "127.0.0.1", "100.64.0.1", "::1", "fe80::1", "nada", "203.0.113.5"]:
            self.assertIsNone(n.target_allowed(bad), bad)

    def test_rate_limiter(self):
        r = n.RateLimiter(per_minute=20, concurrent=1)
        self.assertTrue(r.acquire("a", 10, now=0))
        self.assertFalse(r.acquire("a", 5, now=1))  # já tem uma em andamento
        r.release("a")
        self.assertTrue(r.acquire("a", 10, now=2))
        r.release("a")
        self.assertFalse(r.acquire("a", 1, now=3))  # 20 no minuto
        self.assertTrue(r.acquire("a", 10, now=61))
        self.assertTrue(r.acquire("b", 10, now=3))

    def test_nft_rules_block_private_destinations(self):
        rules = n.nft_rules("10.66.0.0/24", "eth0", "10.66.0.1", 8787)
        self.assertIn("169.254.0.0/16", rules)
        self.assertIn("masquerade", rules)
        self.assertIn('tcp dport 8787 accept', rules)


if __name__ == "__main__":
    unittest.main()
