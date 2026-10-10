// Every test file runs in a realm hardened the way the real host's is (src/main.ts), so the suite proves the host
// still works locked down.
import { hardenRealm } from "../src/harden";

hardenRealm();
