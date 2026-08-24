# Storage compatibility fixtures

- `crc-golden.yata` / `crc-golden.yata-wal` are legacy checksum fixtures that the
  current format must reject.
- `v0.5.0-current.yata` was created by tag `v0.5.0` at commit
  `fae239be99bd3fb7425eaa198acf4b1dae0ad0c9`. It contains two `PreviousRelease`
  vertices, their `name` properties, and one `LINKS` edge. Its SHA-256 is
  `CAD5490C4BB79AF34FCC17D7B3CC4A2F5B57A2E4F47B3D0F13BF54DBA377880C`.

The previous-release fixture is intentionally copied before each test. Tests may append
current-build commits only to the copy and must not rewrite the checked-in fixture.
