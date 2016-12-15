﻿using System;
using System.Drawing.Imaging;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using Rectangle = SharpDX.Rectangle;
using System.Drawing;
using System.Runtime.InteropServices;

namespace DesktopDuplication
{
    /// <summary>
    /// Provides access to frame-by-frame updates of a particular desktop (i.e. one monitor), with image and cursor information.
    /// </summary>
    public class DesktopDuplicator
    {
        private readonly Device _device;
        private readonly Texture2DDescription _textureDescription;
        private OutputDescription _outputDescription;
        private readonly OutputDuplication _deskDupl;

        private Texture2D _desktopImageTexture;
        private OutputDuplicateFrameInformation _frameInfo;
        private readonly int _whichOutputDevice;

        private Bitmap FinalImage { get; set; }

        /// <summary>
        /// Duplicates the output of the specified monitor.
        /// </summary>
        /// <param name="whichMonitor">The output device to duplicate (i.e. monitor). Begins with zero, which seems to correspond to the primary monitor.</param>
        public DesktopDuplicator(int whichMonitor)
            : this(0, whichMonitor)
        {
        }

        /// <summary>
        /// Duplicates the output of the specified monitor on the specified graphics adapter.
        /// </summary>
        /// <param name="whichGraphicsCardAdapter">The adapter which contains the desired outputs.</param>
        /// <param name="whichOutputDevice">The output device to duplicate (i.e. monitor). Begins with zero, which seems to correspond to the primary monitor.</param>
        public DesktopDuplicator(int whichGraphicsCardAdapter, int whichOutputDevice)
        {
            _whichOutputDevice = whichOutputDevice;
            using (var adapter = CreateAdapter(whichGraphicsCardAdapter))
            {
                _device = new Device(adapter);
                using (var output1 = CreateOutput1(whichOutputDevice, adapter))
                {
                    _outputDescription = output1.Description;
                    _textureDescription = new Texture2DDescription()
                    {
                        CpuAccessFlags = CpuAccessFlags.Read,
                        BindFlags = BindFlags.None,
                        Format = Format.B8G8R8A8_UNorm,
                        Width = _outputDescription.DesktopBounds.Width,
                        Height = _outputDescription.DesktopBounds.Height,
                        OptionFlags = ResourceOptionFlags.None,
                        MipLevels = 1,
                        ArraySize = 1,
                        SampleDescription = {Count = 1, Quality = 0},
                        Usage = ResourceUsage.Staging
                    };

                    _deskDupl = CreateOutputDuplication(output1);
                }
            }
        }

        private OutputDuplication CreateOutputDuplication(Output1 output1)
        {
            try
            {
                return output1.DuplicateOutput(_device);
            }
            catch (SharpDXException ex)
            {
                if (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.NotCurrentlyAvailable.Result.Code)
                {
                    throw new DesktopDuplicationException(
                        "There is already the maximum number of applications using the Desktop Duplication API running, please close one of the applications and try again.");
                }
                throw;
            }
        }

        private static Output1 CreateOutput1(int whichOutputDevice, Adapter1 adapter)
        {
            try
            {
                return adapter.GetOutput(whichOutputDevice).QueryInterface<Output1>();
            }
            catch (SharpDXException)
            {
                throw new DesktopDuplicationException("Could not find the specified output device.");
            }
        }

        private static Adapter1 CreateAdapter(int whichGraphicsCardAdapter)
        {
            try
            {
                return new Factory1().GetAdapter1(whichGraphicsCardAdapter);
            }
            catch (SharpDXException)
            {
                throw new DesktopDuplicationException("Could not find the specified graphics card adapter.");
            }
        }

        /// <summary>
        /// Retrieves the latest desktop image and associated metadata.
        /// </summary>
        public DesktopFrame GetLatestFrame()
        {
            // Try to get the latest frame; this may timeout
            var retrievalTimedOut = RetrieveFrame();
            if (retrievalTimedOut)
                return null;

            var frame = new DesktopFrame();
            try
            {
                RetrieveFrameMetadata(frame);
                RetrieveCursorMetadata(frame);
                if (frame.MovedRegions.Length != 0 || frame.UpdatedRegions.Length != 0)
                    ProcessFrame(frame);
            }
            catch
            {
                // ignored
            }
            try
            {
                ReleaseFrame();
            }
            catch
            {
                //    throw new DesktopDuplicationException("Couldn't release frame.");  
            }
            return frame;
        }

        private bool RetrieveFrame()
        {
            try
            {
                _frameInfo = new OutputDuplicateFrameInformation();
                SharpDX.DXGI.Resource desktopResource;
                _deskDupl.AcquireNextFrame(500, out _frameInfo, out desktopResource);
                using (desktopResource)
                {
                    if (_desktopImageTexture == null)
                        _desktopImageTexture = new Texture2D(_device, _textureDescription);

                    using (var tempTexture = desktopResource.QueryInterface<Texture2D>())
                        _device.ImmediateContext.CopyResource(tempTexture, _desktopImageTexture);
                }
                return false;
            }
            catch (SharpDXException ex)
            {
                if (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code)
                {
                    return true;
                }
                if (ex.ResultCode.Failure)
                {
                    throw new DesktopDuplicationException("Failed to acquire next frame.");
                }
                return false;
            }
        }

        private void ReleaseFrame()
        {
            try
            {
                _deskDupl.ReleaseFrame();
            }
            catch (SharpDXException ex)
            {
                if (ex.ResultCode.Failure)
                {
                    throw new DesktopDuplicationException("Failed to release frame.");
                }
            }
        }

        private void RetrieveFrameMetadata(DesktopFrame frame)
        {
            if (_frameInfo.TotalMetadataBufferSize > 0)
            {
                // Get moved regions
                int movedRegionsLength;
                var movedRectangles = new OutputDuplicateMoveRectangle[_frameInfo.TotalMetadataBufferSize];
                _deskDupl.GetFrameMoveRects(movedRectangles.Length, movedRectangles, out movedRegionsLength);
                frame.MovedRegions =
                    new MovedRegion[movedRegionsLength/Marshal.SizeOf(typeof(OutputDuplicateMoveRectangle))];
                for (var i = 0; i < frame.MovedRegions.Length; i++)
                {
                    frame.MovedRegions[i] = new MovedRegion()
                    {
                        Source =
                            new System.Drawing.Point(movedRectangles[i].SourcePoint.X, movedRectangles[i].SourcePoint.Y),
                        Destination =
                            new System.Drawing.Rectangle(movedRectangles[i].DestinationRect.X,
                                movedRectangles[i].DestinationRect.Y, movedRectangles[i].DestinationRect.Width,
                                movedRectangles[i].DestinationRect.Height)
                    };
                }

                // Get dirty regions
                int dirtyRegionsLength;
                var dirtyRectangles = new Rectangle[_frameInfo.TotalMetadataBufferSize];
                _deskDupl.GetFrameDirtyRects(dirtyRectangles.Length, dirtyRectangles, out dirtyRegionsLength);
                frame.UpdatedRegions =
                    new System.Drawing.Rectangle[dirtyRegionsLength/Marshal.SizeOf(typeof(Rectangle))];
                for (var i = 0; i < frame.UpdatedRegions.Length; i++)
                {
                    frame.UpdatedRegions[i] = new System.Drawing.Rectangle(dirtyRectangles[i].X, dirtyRectangles[i].Y,
                        dirtyRectangles[i].Width, dirtyRectangles[i].Height);
                }
            }
            else
            {
                frame.MovedRegions = new MovedRegion[0];
                frame.UpdatedRegions = new System.Drawing.Rectangle[0];
            }
        }

        private void RetrieveCursorMetadata(DesktopFrame frame)
        {
            var pointerInfo = new PointerInfo();

            // A non-zero mouse update timestamp indicates that there is a mouse position update and optionally a shape change
            //if (frameInfo.LastMouseUpdateTime == 0)
            //    return;

            var updatePosition = _frameInfo.PointerPosition.Visible ||
                                 (pointerInfo.WhoUpdatedPositionLast == _whichOutputDevice);

            // If two outputs both say they have a visible, only update if new update has newer timestamp
            if (_frameInfo.PointerPosition.Visible && pointerInfo.Visible &&
                (pointerInfo.WhoUpdatedPositionLast != _whichOutputDevice) &&
                (pointerInfo.LastTimeStamp > _frameInfo.LastMouseUpdateTime))
                updatePosition = false;

            // Update position
            if (updatePosition)
            {
                pointerInfo.Position = new SharpDX.Point(_frameInfo.PointerPosition.Position.X,
                    _frameInfo.PointerPosition.Position.Y);
                pointerInfo.WhoUpdatedPositionLast = _whichOutputDevice;
                pointerInfo.LastTimeStamp = _frameInfo.LastMouseUpdateTime;
                pointerInfo.Visible = _frameInfo.PointerPosition.Visible;
            }

            // No new shape
            if (_frameInfo.PointerShapeBufferSize != 0)
            {
                if (_frameInfo.PointerShapeBufferSize > pointerInfo.PtrShapeBuffer.Length)
                {
                    pointerInfo.PtrShapeBuffer = new byte[_frameInfo.PointerShapeBufferSize];
                }

                try
                {
                    unsafe
                    {
                        fixed (byte* ptrShapeBufferPtr = pointerInfo.PtrShapeBuffer)
                        {
                            int bufferSize;
                            _deskDupl.GetFramePointerShape(_frameInfo.PointerShapeBufferSize, (IntPtr) ptrShapeBufferPtr,
                                out bufferSize, out pointerInfo.ShapeInfo);

                            var width = pointerInfo.ShapeInfo.Width;
                            var height = pointerInfo.ShapeInfo.Height;

                            if (pointerInfo.ShapeInfo.Type == 1) height /= 2;

                            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                            var boundsRect = new System.Drawing.Rectangle(0, 0, width, height);

                            var mapDest = bitmap.LockBits(boundsRect, ImageLockMode.WriteOnly, bitmap.PixelFormat);
                            var sourcePtr = (IntPtr) ptrShapeBufferPtr;
                            var destPtr = mapDest.Scan0;
                            for (var y = 0; y < height; y++)
                            {
                                if (pointerInfo.ShapeInfo.Type == 1)
                                {
                                    for (var x = 0; x < pointerInfo.ShapeInfo.Width;)
                                    {
                                        var color = Marshal.ReadByte(sourcePtr, x/8);
                                        var mask = Marshal.ReadByte(sourcePtr, height*pointerInfo.ShapeInfo.Pitch + x/8);
                                        for (var i = 0; i < 8 && x < width; x++, i++)
                                        {
                                            Int32 rgba = (0 != (mask & 128))
                                                ? (0 != (color & 128) ? -16777216 : - 1)
                                                : 0;
                                            Marshal.WriteInt32(destPtr, x*4, rgba);
                                            color *= 2;
                                            mask *= 2;
                                        }
                                    }
                                }
                                else if (pointerInfo.ShapeInfo.Type == 2 || pointerInfo.ShapeInfo.Type == 4)
                                {
                                    // Copy a single line 
                                    Utilities.CopyMemory(destPtr, sourcePtr, width*4);
                                }

                                // Advance pointers
                                sourcePtr = IntPtr.Add(sourcePtr, pointerInfo.ShapeInfo.Pitch);
                                destPtr = IntPtr.Add(destPtr, mapDest.Stride);
                            }

                            // Release bitmap lock
                            bitmap.UnlockBits(mapDest);
                            frame.CursorBitmap = bitmap;
                        }
                    }
                }
                catch (SharpDXException ex)
                {
                    if (ex.ResultCode.Failure)
                    {
                        throw new DesktopDuplicationException("Failed to get frame pointer shape.");
                    }
                }
            }

            //frame.CursorVisible = pointerInfo.Visible;
            frame.CursorLocation = new System.Drawing.Point(pointerInfo.Position.X, pointerInfo.Position.Y);
        }

        private void ProcessFrame(DesktopFrame frame)
        {
            // Get the desktop capture texture
            var mapSource = _device.ImmediateContext.MapSubresource(_desktopImageTexture, 0, MapMode.Read, MapFlags.None);

            FinalImage = new Bitmap(_outputDescription.DesktopBounds.Width, _outputDescription.DesktopBounds.Height,
                PixelFormat.Format32bppRgb);
            var boundsRect = new System.Drawing.Rectangle(0, 0, _outputDescription.DesktopBounds.Width,
                _outputDescription.DesktopBounds.Height);
            // Copy pixels from screen capture Texture to GDI bitmap
            var mapDest = FinalImage.LockBits(boundsRect, ImageLockMode.WriteOnly, FinalImage.PixelFormat);
            var sourcePtr = mapSource.DataPointer;
            var destPtr = mapDest.Scan0;
            for (var y = 0; y < _outputDescription.DesktopBounds.Height; y++)
            {
                // Copy a single line 
                Utilities.CopyMemory(destPtr, sourcePtr, _outputDescription.DesktopBounds.Width*4);

                // Advance pointers
                sourcePtr = IntPtr.Add(sourcePtr, mapSource.RowPitch);
                destPtr = IntPtr.Add(destPtr, mapDest.Stride);
            }

            // Release source and dest locks
            FinalImage.UnlockBits(mapDest);
            _device.ImmediateContext.UnmapSubresource(_desktopImageTexture, 0);
            frame.DesktopImage = FinalImage;
        }
    }
}