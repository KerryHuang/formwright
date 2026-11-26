#!/usr/bin/env python3
"""RFC 6238 TOTP（HMAC-SHA1），只用標準函式庫。
用法：totp.py <base32 secret> [--step N | --time 秒] [--digits 6]
"""
import argparse
import base64
import hashlib
import hmac
import struct
import time


def totp(secret_b32: str, step: int, digits: int = 6) -> str:
    key = base64.b32decode(secret_b32.strip().replace(" ", "").upper() + "=" * (-len(secret_b32.strip().replace(" ", "")) % 8))
    digest = hmac.new(key, struct.pack(">Q", step), hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    value = struct.unpack(">I", digest[offset:offset + 4])[0] & 0x7FFFFFFF
    return str(value % (10 ** digits)).zfill(digits)


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("secret")
    p.add_argument("--step", type=int)
    p.add_argument("--time", type=int)
    p.add_argument("--digits", type=int, default=6)
    a = p.parse_args()
    step = a.step if a.step is not None else (a.time if a.time is not None else int(time.time())) // 30
    print(totp(a.secret, step, a.digits))


if __name__ == "__main__":
    main()
