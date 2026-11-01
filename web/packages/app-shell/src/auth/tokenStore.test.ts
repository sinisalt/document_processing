import { describe, expect, it } from "vitest";
import { createTokenStore, DEFAULT_TOKEN_KEY, EXPIRY_SKEW_MS } from "./tokenStore";

class MemoryStorage implements Storage {
  private map = new Map<string, string>();
  get length() {
    return this.map.size;
  }
  clear() {
    this.map.clear();
  }
  getItem(key: string) {
    return this.map.get(key) ?? null;
  }
  key(index: number) {
    return [...this.map.keys()][index] ?? null;
  }
  removeItem(key: string) {
    this.map.delete(key);
  }
  setItem(key: string, value: string) {
    this.map.set(key, value);
  }
}

describe("token store", () => {
  it("round-trips a token with its expiry", () => {
    let clock = 1_000_000;
    const storage = new MemoryStorage();
    const store = createTokenStore(storage, { now: () => clock });
    const saved = store.save({ accessToken: "abc", expiresIn: 3600, username: "tenant-a-admin", displayName: "Anna Admin" });
    expect(saved.expiresAt).toBe(clock + 3_600_000);
    expect(store.load()).toEqual({ accessToken: "abc", expiresAt: clock + 3_600_000, username: "tenant-a-admin", displayName: "Anna Admin" });
    expect(store.remainingMs()).toBe(3_600_000 - EXPIRY_SKEW_MS);
    clock += 1_000;
    expect(store.remainingMs()).toBe(3_600_000 - EXPIRY_SKEW_MS - 1_000);
  });

  it("drops the token once it expires, with the safety margin", () => {
    let clock = 0;
    const storage = new MemoryStorage();
    const store = createTokenStore(storage, { now: () => clock });
    store.save({ accessToken: "abc", expiresIn: 60 });
    clock = 60_000 - EXPIRY_SKEW_MS - 1;
    expect(store.load()).not.toBeNull();
    clock = 60_000 - EXPIRY_SKEW_MS;
    expect(store.load()).toBeNull();
    expect(storage.getItem(DEFAULT_TOKEN_KEY)).toBeNull();
    expect(store.remainingMs()).toBe(0);
  });

  it("clear removes the token", () => {
    const storage = new MemoryStorage();
    const store = createTokenStore(storage, { now: () => 0 });
    store.save({ accessToken: "abc", expiresIn: 60 });
    store.clear();
    expect(store.load()).toBeNull();
    expect(storage.length).toBe(0);
  });

  it("ignores corrupt or foreign entries", () => {
    const storage = new MemoryStorage();
    storage.setItem(DEFAULT_TOKEN_KEY, "{not json");
    const store = createTokenStore(storage, { now: () => 0 });
    expect(store.load()).toBeNull();
    storage.setItem(DEFAULT_TOKEN_KEY, JSON.stringify({ accessToken: "", expiresAt: 10 }));
    expect(store.load()).toBeNull();
    storage.setItem(DEFAULT_TOKEN_KEY, JSON.stringify({ accessToken: "x", expiresAt: "soon" }));
    expect(store.load()).toBeNull();
  });

  it("works without storage (memory only)", () => {
    const store = createTokenStore(null, { now: () => 0 });
    store.save({ accessToken: "abc", expiresIn: 60 });
    expect(store.load()?.accessToken).toBe("abc");
    store.clear();
    expect(store.load()).toBeNull();
  });

  it("uses its own key so several stores can share a storage", () => {
    const storage = new MemoryStorage();
    const a = createTokenStore(storage, { key: "a", now: () => 0 });
    const b = createTokenStore(storage, { key: "b", now: () => 0 });
    a.save({ accessToken: "A", expiresIn: 60 });
    expect(b.load()).toBeNull();
    expect(a.load()?.accessToken).toBe("A");
  });
});
