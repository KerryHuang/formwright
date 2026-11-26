"""totp.py 的測試：python3 -I scripts/installer/e2e/totp_test.py（RFC 6238 附錄 B 的 SHA1 測試向量）"""
import base64
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from totp import totp  # noqa: E402

SECRET = base64.b32encode(b"12345678901234567890").decode()


class Rfc6238(unittest.TestCase):
    def test_vectors(self):
        for t, expected in [(59, "94287082"), (1111111109, "07081804"), (1111111111, "14050471"), (1234567890, "89005924"), (2000000000, "69279037")]:
            self.assertEqual(totp(SECRET, t // 30, 8), expected, t)

    def test_six_digits_is_suffix(self):
        self.assertEqual(totp(SECRET, 59 // 30), "287082")

    def test_unpadded_and_spaced_secret(self):
        self.assertEqual(totp(SECRET.rstrip("=") .lower(), 1, 6), totp(SECRET, 1, 6))


if __name__ == "__main__":
    unittest.main()
