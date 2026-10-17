"""Check SteamVR's shared texture, using an independent observer D3D device.

Reading the producer resource would flush its pending OpenVR copy and mask
the last-frame latency bug. This test must read on a second device instead.
The test overlay stays hidden and never captures user content.
"""
import ctypes as C
import json
import time
import numpy as np
import openvr
from vr_overlay import SubtitleOverlay
from d3d_texture import D3DTexturePool, PTR, UINT, HRESULT, method, release


def main():
    overlay = SubtitleOverlay('local.doubao.subtitle.latency-check')
    observer = None
    view, resource = PTR(), PTR()
    try:
        observer = D3DTexturePool(openvr.VRSystem().getDXGIOutputInfo())
        initial = np.full((200, 610, 4), [10, 40, 80, 255], dtype=np.uint8)
        assert overlay.submit(initial, show=False)
        reference = observer.upload(initial)
        # Call the header's ID3D11Resource* signature directly; the generated
        # Python binding incorrectly wraps that input and release handle byref.
        get_texture = C.WINFUNCTYPE(HRESULT, C.c_uint64, C.POINTER(PTR), PTR,
            C.POINTER(UINT), C.POINTER(UINT), C.POINTER(UINT), C.POINTER(C.c_int),
            C.POINTER(C.c_int), C.POINTER(openvr.VRTextureBounds_t))(
                C.cast(overlay.api.function_table.getOverlayTexture, PTR).value)
        width, height, fmt, api, space = UINT(), UINT(), UINT(), C.c_int(), C.c_int()
        bounds = openvr.VRTextureBounds_t()
        error = get_texture(overlay.handle, C.byref(view), reference,
            C.byref(width), C.byref(height), C.byref(fmt), C.byref(api), C.byref(space), C.byref(bounds))
        if error:
            raise RuntimeError(f'GetOverlayTexture failed: {error}')
        method(view, 7, None, C.POINTER(PTR))(view, C.byref(resource))
        latencies = []
        for index in range(12):
            expected = np.full_like(initial, [index * 20, 230 - index * 10, 120, 255])
            start = time.perf_counter()
            assert overlay.submit(expected, show=False)
            # No further producer GPU calls until we have seen this frame.
            while True:
                actual = observer.readback(resource)
                if np.array_equal(actual, expected):
                    break
                if time.perf_counter() - start > .5:
                    raise AssertionError('SteamVR still holds the previous subtitle after 500 ms')
                time.sleep(.002)
            latencies.append((time.perf_counter() - start) * 1000)
            time.sleep(.08)
        time.sleep(.3)
        np.testing.assert_array_equal(observer.readback(resource), expected)
        assert not overlay.api.isOverlayVisible(overlay.handle)
        print(json.dumps(dict(frames=len(latencies), all_latest_without_next_update=True,
            median_ms=float(np.median(latencies)), p95_ms=float(np.percentile(latencies, 95)),
            max_ms=max(latencies), final_static_frame_matches=True, visible=False)))
    finally:
        release(resource)
        if view:
            release_handle = C.WINFUNCTYPE(HRESULT, C.c_uint64, PTR)(
                C.cast(overlay.api.function_table.releaseNativeOverlayHandle, PTR).value)
            release_handle(overlay.handle, view)
        if observer:
            observer.close()
        overlay.close()


if __name__ == '__main__':
    main()
