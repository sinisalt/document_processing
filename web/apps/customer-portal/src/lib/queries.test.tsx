import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { notifyUploaded, UPLOAD_POLL_WINDOW_MS, useRecentUpload } from "./queries";

describe("useRecentUpload", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-30T12:00:00Z"));
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("turns on when an upload is reported and off once the window has passed", () => {
    const { result } = renderHook(() => useRecentUpload());
    expect(result.current).toBe(false);

    act(() => notifyUploaded());
    expect(result.current).toBe(true);

    act(() => vi.advanceTimersByTime(UPLOAD_POLL_WINDOW_MS - 1_000));
    expect(result.current).toBe(true);
    // A second upload restarts the window.
    act(() => notifyUploaded());
    act(() => vi.advanceTimersByTime(UPLOAD_POLL_WINDOW_MS - 1_000));
    expect(result.current).toBe(true);
    act(() => vi.advanceTimersByTime(1_000));
    expect(result.current).toBe(false);
  });

  it("starts on for a list mounted during the window (navigating to another tab right after an upload)", () => {
    act(() => notifyUploaded());
    act(() => vi.advanceTimersByTime(10_000));
    const { result } = renderHook(() => useRecentUpload());
    expect(result.current).toBe(true);
    act(() => vi.advanceTimersByTime(UPLOAD_POLL_WINDOW_MS - 10_000));
    expect(result.current).toBe(false);
  });
});
