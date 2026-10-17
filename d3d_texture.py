"""Small D3D11 uploader: persistent textures, no per-frame raw-image loading.

COM slots follow Microsoft's d3d11.h and dxgi.h. All calls belong to the
controller thread; the WGC worker only places copied pixels in its mailbox.
"""
import ctypes as C
from ctypes import wintypes as W
import time
import uuid
import numpy as np

PTR = C.c_void_p
UINT = C.c_uint32
HRESULT = C.c_int32


def method(pointer, slot, result, *arguments):
    table = C.cast(pointer, C.POINTER(C.POINTER(PTR))).contents
    return C.WINFUNCTYPE(result, PTR, *arguments)(table[slot])


def check(hr, operation):
    if hr < 0:
        raise RuntimeError(f"{operation}失败：0x{hr & 0xffffffff:08X}")


def release(pointer):
    if pointer:
        method(pointer, 2, UINT)(pointer)


class SampleDesc(C.Structure):
    _fields_ = [("Count", UINT), ("Quality", UINT)]


class TextureDesc(C.Structure):
    _fields_ = [("Width", UINT), ("Height", UINT), ("MipLevels", UINT),
                ("ArraySize", UINT), ("Format", UINT), ("SampleDesc", SampleDesc),
                ("Usage", UINT), ("BindFlags", UINT), ("CPUAccessFlags", UINT), ("MiscFlags", UINT)]


class MappedResource(C.Structure):
    _fields_ = [("pData", PTR), ("RowPitch", UINT), ("DepthPitch", UINT)]


class D3DTexturePool:
    def __init__(self, adapter_index=0):
        self.device = PTR()
        self.context = PTR()
        self.query = PTR()
        self.textures = []
        self.retired = []
        self.shape = None
        self.index = -1
        self.allocations = 0
        factory, adapter = PTR(), PTR()
        try:
            dxgi = C.WinDLL("dxgi")
            dxgi.CreateDXGIFactory1.argtypes = [PTR, C.POINTER(PTR)]
            dxgi.CreateDXGIFactory1.restype = HRESULT
            iid = (C.c_ubyte * 16).from_buffer_copy(uuid.UUID("770aae78-f26f-4dba-a829-253c83d1b387").bytes_le)
            check(dxgi.CreateDXGIFactory1(C.byref(iid), C.byref(factory)), "创建 DXGI")
            check(method(factory, 12, HRESULT, UINT, C.POINTER(PTR))(factory, max(0, adapter_index), C.byref(adapter)), "选择 VR 显卡")
            d3d = C.WinDLL("d3d11")
            d3d.D3D11CreateDevice.argtypes = [PTR, UINT, PTR, UINT, PTR, UINT, UINT, C.POINTER(PTR), PTR, C.POINTER(PTR)]
            d3d.D3D11CreateDevice.restype = HRESULT
            check(d3d.D3D11CreateDevice(adapter, 0, None, 0x20, None, 0, 7, C.byref(self.device), None, C.byref(self.context)), "创建字幕 GPU 设备")
            query_desc = (UINT * 2)(0, 0)  # D3D11_QUERY_EVENT
            check(method(self.device, 24, HRESULT, PTR, C.POINTER(PTR))(self.device, query_desc, C.byref(self.query)), "创建 GPU 完成事件")
            self.update = method(self.context, 48, None, PTR, UINT, PTR, PTR, UINT, UINT)
            self.end = method(self.context, 28, None, PTR)
            self.get_data = method(self.context, 29, HRESULT, PTR, PTR, UINT, UINT)
            self.flush = method(self.context, 111, None)
        except Exception:
            self.close()
            raise
        finally:
            release(adapter)
            release(factory)

    def make_texture(self, width, height, staging=False):
        desc = TextureDesc(width, height, 1, 1, 28, SampleDesc(1, 0),
                           3 if staging else 0, 0 if staging else 8,
                           0x20000 if staging else 0, 0)
        texture = PTR()
        check(method(self.device, 5, HRESULT, C.POINTER(TextureDesc), PTR, C.POINTER(PTR))(
            self.device, C.byref(desc), None, C.byref(texture)), "创建字幕纹理")
        self.allocations += 1
        return texture

    def upload(self, rgba):
        rgba = np.ascontiguousarray(rgba, dtype=np.uint8)
        height, width, channels = rgba.shape
        if channels != 4:
            raise ValueError("GPU 上传要求 RGBA 像素")
        if self.shape != (height, width):
            fresh = []
            try:
                for _ in range(3):
                    fresh.append(self.make_texture(width, height))
            except Exception:
                for texture in fresh:
                    release(texture)
                raise
            self.retired.extend((time.monotonic(), texture) for texture in self.textures)
            self.textures = fresh
            self.shape = height, width
            self.index = -1
        self.index = (self.index + 1) % len(self.textures)
        texture = self.textures[self.index]
        self.update(self.context, texture, 0, None, PTR(rgba.ctypes.data), width * 4, 0)
        self.end(self.context, self.query)
        self.flush(self.context)
        complete = W.BOOL()
        deadline = time.monotonic() + 0.25
        while True:
            hr = self.get_data(self.context, self.query, C.byref(complete), C.sizeof(complete), 0)
            check(hr, "等待字幕纹理就绪")
            if hr == 0 and complete.value:
                break
            if time.monotonic() >= deadline:
                # Preserve the last submitted VR image rather than submit a
                # partially uploaded or blank replacement texture.
                return None
            time.sleep(0.0005)
        alive = []
        for retired_at, previous in self.retired:
            if time.monotonic() - retired_at > 2:
                release(previous)
            else:
                alive.append((retired_at, previous))
        self.retired = alive
        return texture

    def readback(self, texture):
        height, width = self.shape
        staging = self.make_texture(width, height, staging=True)
        mapped = MappedResource()
        try:
            method(self.context, 47, None, PTR, PTR)(self.context, staging, texture)
            check(method(self.context, 14, HRESULT, PTR, UINT, UINT, UINT, C.POINTER(MappedResource))(
                self.context, staging, 0, 1, 0, C.byref(mapped)), "读取 GPU 验证图像")
            try:
                raw = np.ctypeslib.as_array((C.c_ubyte * (mapped.RowPitch * height)).from_address(mapped.pData))
                return raw.reshape(height, mapped.RowPitch)[:, :width * 4].copy().reshape(height, width, 4)
            finally:
                method(self.context, 15, None, PTR, UINT)(self.context, staging, 0)
        finally:
            release(staging)

    def close(self):
        for texture in self.textures:
            release(texture)
        for _, texture in self.retired:
            release(texture)
        self.textures = []
        self.retired = []
        release(self.query)
        release(self.context)
        release(self.device)
        self.query = self.context = self.device = PTR()
