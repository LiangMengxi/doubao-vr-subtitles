"""Standalone OpenVR overlay; no VRChat hooks or process injection."""
import ctypes
import math
import logging
import atexit
import openvr
from d3d_texture import D3DTexturePool


class SubtitleOverlay:
    def __init__(self, key="local.doubao.subtitle.bridge"):
        self.handle = None
        self.initialized = False
        self.pool = None
        self.shown = False
        try:
            openvr.init(openvr.VRApplication_Overlay)
            self.initialized = True
            self.api = openvr.VROverlay()
            self.handle = self.api.createOverlay(key, "Doubao VR Subtitles")
            self.api.setOverlayInputMethod(self.handle, openvr.VROverlayInputMethod_None)
            self.pool = D3DTexturePool(openvr.VRSystem().getDXGIOutputInfo())
            atexit.register(self.close)
        except Exception:
            self.close()
            raise

    def configure(self, width=1.0, distance=1.2, x=0.0, y=-0.32, opacity=1.0, pitch=0.0):
        transform = openvr.HmdMatrix34_t()
        radians = math.radians(pitch)
        c, s = math.cos(radians), math.sin(radians)
        rows = [[1, 0, 0, x], [0, c, -s, y], [0, s, c, -distance]]
        for i in range(3):
            for j in range(4):
                transform.m[i][j] = rows[i][j]
        self.api.setOverlayWidthInMeters(self.handle, width)
        self.api.setOverlayAlpha(self.handle, opacity)
        self.api.setOverlayTransformTrackedDeviceRelative(self.handle, openvr.k_unTrackedDeviceIndex_Hmd, transform)

    def submit(self, rgba, show=True):
        texture = self.pool.upload(rgba)
        if texture is None:
            return False
        descriptor = openvr.Texture_t(texture.value, openvr.TextureType_DirectX, openvr.ColorSpace_Auto)
        self.api.setOverlayTexture(self.handle, descriptor)
        # SetOverlayTexture enqueues a copy on the producing D3D context.
        # Flush AFTER it: flushing only in upload leaves the final copy queued
        # until the next subtitle update, which can be several seconds later.
        self.pool.flush(self.pool.context)
        if show and not self.shown:
            self.api.showOverlay(self.handle)
            self.shown = True
        return True

    def hide(self):
        if self.handle is not None:
            self.api.hideOverlay(self.handle)
            self.shown = False

    def quit_requested(self):
        event = openvr.VREvent_t()
        # The Python binding returns (has_event, event), not a bare boolean.
        # Bound draining so a noisy runtime cannot monopolize the GUI thread.
        for _ in range(32):
            has_event, event = self.api.pollNextOverlayEvent(self.handle, event)
            if not has_event:
                return False
            if event.eventType == openvr.VREvent_Quit:
                openvr.VRSystem().acknowledgeQuit_Exiting()
                return True
        return False

    def close(self):
        atexit.unregister(self.close)
        try:
            if self.handle is not None:
                for operation in (self.api.hideOverlay, self.api.clearOverlayTexture, self.api.destroyOverlay):
                    try:
                        operation(self.handle)
                    except Exception:
                        logging.exception("Overlay cleanup operation failed")
        finally:
            self.handle = None
            self.shown = False
            if self.pool:
                try:
                    self.pool.close()
                except Exception:
                    logging.exception("GPU texture cleanup failed")
                self.pool = None
            if self.initialized:
                self.initialized = False
                try:
                    openvr.shutdown()
                except Exception:
                    logging.exception("OpenVR shutdown failed")
