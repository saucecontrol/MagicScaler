using System;
using System.IO;
using System.Numerics;
using System.ComponentModel;

using PhotoSauce.Interop.Wic;
using PhotoSauce.MagicScaler.Transforms;

namespace PhotoSauce.MagicScaler
{
	/// <summary>Provides a set of methods for constructing a MagicScaler processing pipeline or for all-at-once processing of an image.</summary>
	public static class MagicImageProcessor
	{
		/// <summary>True to allow <a href="https://en.wikipedia.org/wiki/YCbCr">Y'CbCr</a> images to be processed in their native planar format, false to force RGB conversion before processing.</summary>
		/// <include file='Docs/Remarks.xml' path='doc/member[@name="EnablePlanarPipeline"]/*'/>
		/// <value>Default value: <c>true</c></value>
		public static bool EnablePlanarPipeline { get; set; } = true;

		/// <summary>True to check for <c>Orientation</c> tag in XMP metadata in addition to the default Exif metadata location, false to check Exif only.</summary>
		/// <value>Default value: <c>false</c></value>
		public static bool EnableXmpOrientation { get; set; } = false;

		/// <summary>True to enable internal <see cref="IPixelSource"/> instrumentation, false to disable.  When disabled, no <see cref="PixelSourceStats" /> will be collected for the pipeline stages.</summary>
		/// <value>Default value: <c>false</c></value>
		public static bool EnablePixelSourceStats { get; set; } = false;

		/// <summary>Overrides the default <a href="https://en.wikipedia.org/wiki/SIMD">SIMD</a> support detection to force floating point processing on or off.</summary>
		/// <include file='Docs/Remarks.xml' path='doc/member[@name="EnableSimd"]/*'/>
		/// <value>Default value: <c>true</c> if the runtime/JIT and hardware support hardware-accelerated <see cref="System.Numerics.Vector{T}" />, otherwise <c>false</c></value>
		[EditorBrowsable(EditorBrowsableState.Never)]
		public static bool EnableSimd { get; set; } = Vector.IsHardwareAccelerated && (Vector<float>.Count == 4 || Vector<float>.Count == 8);

		private static void checkInStream(Stream imgStream)
		{
			if (imgStream is null) throw new ArgumentNullException(nameof(imgStream));
			if (!imgStream.CanSeek || !imgStream.CanRead) throw new ArgumentException("Input Stream must allow Seek and Read", nameof(imgStream));
			if (imgStream.Length <= 0 || imgStream.Position >= imgStream.Length) throw new ArgumentException("Input Stream is empty or positioned at its end", nameof(imgStream));
		}

		private static void checkOutStream(Stream outStream)
		{
			if (outStream is null) throw new ArgumentNullException(nameof(outStream));
			if (!outStream.CanSeek || !outStream.CanWrite) throw new ArgumentException("Output Stream must allow Seek and Write", nameof(outStream));
		}

		/// <summary>All-in-one processing of an image according to the specified <paramref name="settings" />.</summary>
		/// <param name="imgPath">The path to a file containing the input image.</param>
		/// <param name="outStream">The stream to which the output image will be written. The stream must allow Seek and Write.</param>
		/// <param name="settings">The settings for this processing operation.</param>
		/// <returns>A <see cref="ProcessImageResult" /> containing the settings used and basic instrumentation for the pipeline.</returns>
		public static ProcessImageResult ProcessImage(string imgPath, Stream outStream, ProcessImageSettings settings)
		{
			if (imgPath is null) throw new ArgumentNullException(nameof(imgPath));
			if (settings is null) throw new ArgumentNullException(nameof(settings));
			checkOutStream(outStream);

			using var ctx = new PipelineContext(settings);
			ctx.ImageContainer = WicImageDecoder.Load(imgPath, ctx);

			buildPipeline(ctx);
			return WriteOutput(ctx, outStream);
		}

#pragma warning disable 1573 // not all params have docs

		/// <inheritdoc cref="ProcessImage(string, Stream, ProcessImageSettings)" />
		/// <param name="imgBuffer">A buffer containing a supported input image container.</param>
		unsafe public static ProcessImageResult ProcessImage(ReadOnlySpan<byte> imgBuffer, Stream outStream, ProcessImageSettings settings)
		{
			if (imgBuffer == default) throw new ArgumentNullException(nameof(imgBuffer));
			if (settings is null) throw new ArgumentNullException(nameof(settings));
			checkOutStream(outStream);

			fixed (byte* pbBuffer = imgBuffer)
			{
				using var ctx = new PipelineContext(settings);
				ctx.ImageContainer = WicImageDecoder.Load(pbBuffer, imgBuffer.Length, ctx);

				buildPipeline(ctx);
				return WriteOutput(ctx, outStream);
			}
		}

		/// <inheritdoc cref="ProcessImage(string, Stream, ProcessImageSettings)" />
		/// <param name="imgStream">A stream containing a supported input image container. The stream must allow Seek and Read.</param>
		public static ProcessImageResult ProcessImage(Stream imgStream, Stream outStream, ProcessImageSettings settings)
		{
			if (settings is null) throw new ArgumentNullException(nameof(settings));
			checkInStream(imgStream);
			checkOutStream(outStream);

			using var ctx = new PipelineContext(settings);
			ctx.ImageContainer = WicImageDecoder.Load(imgStream, ctx);

			buildPipeline(ctx);
			return WriteOutput(ctx, outStream);
		}

		/// <inheritdoc cref="ProcessImage(string, Stream, ProcessImageSettings)" />
		/// <param name="imgSource">A custom pixel source to use as input.</param>
		public static ProcessImageResult ProcessImage(IPixelSource imgSource, Stream outStream, ProcessImageSettings settings)
		{
			if (imgSource is null) throw new ArgumentNullException(nameof(imgSource));
			if (settings is null) throw new ArgumentNullException(nameof(settings));
			checkOutStream(outStream);

			using var ctx = new PipelineContext(settings) {
				ImageContainer = new PixelSourceContainer(imgSource),
				Source = imgSource.AsPixelSource()
			};

			buildPipeline(ctx);
			return WriteOutput(ctx, outStream);
		}

		/// <inheritdoc cref="ProcessImage(string, Stream, ProcessImageSettings)" />
		/// <param name="imgContainer">A custom <see cref="IImageContainer"/> to use as input.</param>
		public static ProcessImageResult ProcessImage(IImageContainer imgContainer, Stream outStream, ProcessImageSettings settings)
		{
			if (imgContainer is null) throw new ArgumentNullException(nameof(imgContainer));
			if (settings is null) throw new ArgumentNullException(nameof(settings));
			checkOutStream(outStream);

			using var ctx = new PipelineContext(settings) {
				ImageContainer = imgContainer
			};

			buildPipeline(ctx);
			return WriteOutput(ctx, outStream);
		}

		/// <summary>Constructs a new processing pipeline from which pixels can be retrieved.</summary>
		/// <param name="imgPath">The path to a file containing the input image.</param>
		/// <param name="settings">The settings for this processing operation.</param>
		/// <returns>A <see cref="ProcessingPipeline" /> containing the <see cref="IPixelSource" />, settings used, and basic instrumentation for the pipeline.</returns>
		public static ProcessingPipeline BuildPipeline(string imgPath, ProcessImageSettings settings)
		{
			if (imgPath is null) throw new ArgumentNullException(nameof(imgPath));
			if (settings is null) throw new ArgumentNullException(nameof(settings));

			var ctx = new PipelineContext(settings);
			ctx.ImageContainer = WicImageDecoder.Load(imgPath, ctx);

			buildPipeline(ctx, false);
			return new ProcessingPipeline(ctx);
		}

		/// <inheritdoc cref="BuildPipeline(string, ProcessImageSettings)" />
		/// <param name="imgBuffer">A buffer containing a supported input image container.</param>
		unsafe public static ProcessingPipeline BuildPipeline(ReadOnlySpan<byte> imgBuffer, ProcessImageSettings settings)
		{
			if (imgBuffer == default) throw new ArgumentNullException(nameof(imgBuffer));
			if (settings is null) throw new ArgumentNullException(nameof(settings));

			fixed (byte* pbBuffer = imgBuffer)
			{
				var ctx = new PipelineContext(settings);
				ctx.ImageContainer = WicImageDecoder.Load(pbBuffer, imgBuffer.Length, ctx, true);

				buildPipeline(ctx, false);
				return new ProcessingPipeline(ctx);
			}
		}

		/// <inheritdoc cref="BuildPipeline(string, ProcessImageSettings)" />
		/// <param name="imgStream">A stream containing a supported input image container. The stream must allow Seek and Read.</param>
		public static ProcessingPipeline BuildPipeline(Stream imgStream, ProcessImageSettings settings)
		{
			if (settings is null) throw new ArgumentNullException(nameof(settings));
			checkInStream(imgStream);

			var ctx = new PipelineContext(settings);
			ctx.ImageContainer = WicImageDecoder.Load(imgStream, ctx);

			buildPipeline(ctx, false);
			return new ProcessingPipeline(ctx);
		}

		/// <inheritdoc cref="BuildPipeline(string, ProcessImageSettings)" />
		/// <param name="imgSource">A custom pixel source to use as input.</param>
		public static ProcessingPipeline BuildPipeline(IPixelSource imgSource, ProcessImageSettings settings)
		{
			if (imgSource is null) throw new ArgumentNullException(nameof(imgSource));
			if (settings is null) throw new ArgumentNullException(nameof(settings));

			var ctx = new PipelineContext(settings) {
				ImageContainer = new PixelSourceContainer(imgSource),
				Source = imgSource.AsPixelSource()
			};

			buildPipeline(ctx, false);
			return new ProcessingPipeline(ctx);
		}

		/// <inheritdoc cref="BuildPipeline(string, ProcessImageSettings)" />
		/// <param name="imgContainer">A custom <see cref="IImageContainer"/> to use as input.</param>
		public static ProcessingPipeline BuildPipeline(IImageContainer imgContainer, ProcessImageSettings settings)
		{
			if (imgContainer is null) throw new ArgumentNullException(nameof(imgContainer));
			if (settings is null) throw new ArgumentNullException(nameof(settings));

			var ctx = new PipelineContext(settings) {
				ImageContainer = imgContainer
			};

			buildPipeline(ctx, false);
			return new ProcessingPipeline(ctx);
		}

#pragma warning restore 1573

		internal static ProcessImageResult WriteOutput(PipelineContext ctx, Stream ostm)
		{
			MagicTransforms.AddExternalFormatConverter(ctx);
			WicTransforms.AddIndexedColorConverter(ctx);

			var enc = new WicImageEncoder(ctx, ostm.AsIStream());
			enc.WriteSource(ctx);

			return new ProcessImageResult(ctx.UsedSettings, ctx.Stats);
		}

		private static void buildPipeline(PipelineContext ctx, bool outputPlanar = true)
		{
			ctx.ImageFrame = ctx.ImageContainer.GetFrame(ctx.Settings.FrameIndex);

			bool processPlanar = false;
			var wicFrame = ctx.ImageFrame as WicImageFrame;

			if (wicFrame != null)
			{
				processPlanar = EnablePlanarPipeline && wicFrame.SupportsPlanarProcessing && ctx.Settings.Interpolation.WeightingFunction.Support >= 0.5;
				bool profilingPassThrough = processPlanar || (wicFrame.SupportsNativeScale && ctx.Settings.HybridScaleRatio > 1);
				ctx.Source = wicFrame.WicSource.AsPixelSource(nameof(IWICBitmapFrameDecode), !profilingPassThrough);
			}
			else if (ctx.ImageFrame is IYccImageFrame planarFrame)
			{
				processPlanar = true;
				outputPlanar = outputPlanar && planarFrame.IsFullRange && planarFrame.RgbYccMatrix.IsRouglyEqualTo(YccMatrix.Rec601);
				ctx.PlanarContext = new PipelineContext.PlanarPipelineContext(planarFrame.PixelSource.AsPixelSource(), planarFrame.PixelSourceCb.AsPixelSource(), planarFrame.PixelSourceCr.AsPixelSource());
				ctx.Source = ctx.PlanarContext.SourceY;
			}

			MagicTransforms.AddColorProfileReader(ctx);

			ctx.FinalizeSettings();
			ctx.Settings.UnsharpMask = ctx.UsedSettings.UnsharpMask;
			ctx.Settings.JpegQuality = ctx.UsedSettings.JpegQuality;
			ctx.Settings.JpegSubsampleMode = ctx.UsedSettings.JpegSubsampleMode;

			var subsample = ctx.Settings.JpegSubsampleMode;

			if (processPlanar)
			{
				if (wicFrame != null && !ctx.Settings.AutoCrop && ctx.Settings.HybridScaleRatio == 1)
				{
					var orCrop = PixelArea.FromGdiRect(ctx.Settings.Crop).DeOrient(ctx.Orientation, ctx.Source.Width, ctx.Source.Height);

					if (wicFrame.ChromaSubsampling.IsSubsampledX() && ((orCrop.X & 1) != 0 || (orCrop.Width & 1) != 0))
						processPlanar = false;
					if (wicFrame.ChromaSubsampling.IsSubsampledY() && ((orCrop.Y & 1) != 0 || (orCrop.Height & 1) != 0))
						processPlanar = false;
				}

				if (ctx.Settings.SaveFormat == FileFormat.Jpeg && ctx.Orientation.SwapsDimensions())
				{
					if (subsample.IsSubsampledX() && (ctx.Settings.InnerSize.Width & 1) != 0)
						outputPlanar = false;
					if (subsample.IsSubsampledY() && (ctx.Settings.InnerSize.Height & 1) != 0)
						outputPlanar = false;
				}
			}

			if (processPlanar)
			{
				bool savePlanar = outputPlanar
					&& ctx.Settings.SaveFormat == FileFormat.Jpeg
					&& ctx.Settings.OuterSize == ctx.Settings.InnerSize
					&& ctx.DestColorProfile == ctx.SourceColorProfile;

				if (wicFrame != null)
					WicTransforms.AddPlanarCache(ctx);

				MagicTransforms.AddPlanarCropper(ctx);
				MagicTransforms.AddPlanarHybridScaler(ctx);
				MagicTransforms.AddPlanarHighQualityScaler(ctx, savePlanar ? subsample : ChromaSubsampleMode.Subsample444);
				MagicTransforms.AddUnsharpMask(ctx);

				if (savePlanar)
				{
					MagicTransforms.AddPlanarExifFlipRotator(ctx);
					MagicTransforms.AddPlanarExternalFormatConverter(ctx);
				}
				else
				{
					MagicTransforms.AddPlanarConverter(ctx);
					MagicTransforms.AddColorspaceConverter(ctx);
					MagicTransforms.AddExifFlipRotator(ctx);
					MagicTransforms.AddPad(ctx);
				}
			}
			else
			{
				WicTransforms.AddNativeScaler(ctx);
				MagicTransforms.AddCropper(ctx);
				MagicTransforms.AddHybridScaler(ctx);
				WicTransforms.AddPixelFormatConverter(ctx);
				MagicTransforms.AddHybridScaler(ctx);
				MagicTransforms.AddHighQualityScaler(ctx);
				MagicTransforms.AddColorspaceConverter(ctx);
				MagicTransforms.AddMatte(ctx);
				MagicTransforms.AddUnsharpMask(ctx);
				MagicTransforms.AddExifFlipRotator(ctx);
				MagicTransforms.AddPad(ctx);
			}
		}
	}
}
