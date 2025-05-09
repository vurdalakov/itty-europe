// IttyEurope
// A small experimental program that receives and decodes the Europe News RTTY channel from the Internet Teletype.
// https://github.com/vurdalakov/itty-europe
//
// Copyright (c) 2025 Vurdalakov. All rights reserved.
// Distributed under the terms of the MIT License.

namespace Vurdalakov.Rtty
{
    using System;
    using System.Diagnostics;
    using System.IO;

    using NAudio.Wave;

    internal class Program
    {
        public static void Main(String[] args)
        {
            Console.WriteLine();
            try
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(Environment.ProcessPath);
                Console.WriteLine($"{versionInfo.ProductName} v{versionInfo.ProductMajorPart}.{versionInfo.ProductMinorPart}");
            }
            catch
            {
            }
            Console.WriteLine("https://github.com/vurdalakov/itty-europe");
            Console.WriteLine();

            var url = 1 == args.Length ? args[0] : "http://internet-tty.net:8040/EUROPE";
            Console.WriteLine($"URL: '{url}'");

            using var mediaFoundationReader = new MediaFoundationReader(url);
            using var wasapiOut = new WasapiOut();
            using var capture = new WasapiLoopbackCapture();

            var bitsPerSample = capture.WaveFormat.BitsPerSample;
            var bytesPerSample = bitsPerSample / 8 * capture.WaveFormat.Channels;
            var sampleRate = capture.WaveFormat.SampleRate;
            var numberOfChannels = capture.WaveFormat.Channels;

            Console.WriteLine($"Capture format: '{capture.WaveFormat}'");
            Console.WriteLine($"Bits per sample: {bitsPerSample}");
            Console.WriteLine($"Bytes per sample: {bytesPerSample}");
            Console.WriteLine($"Sample rate: {sampleRate:N0}");
            Console.WriteLine($"Number of channels: {numberOfChannels}");

            var outputFilePath = Path.Combine(AppContext.BaseDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}_europe.txt");
            using var streamWriter = File.CreateText(outputFilePath);

            var rttyDecoder = new RttyDecoder(sampleRate, bytesPerSample, numberOfChannels, streamWriter);
            rttyDecoder.Start();

            capture.DataAvailable += (s, a) => rttyDecoder.ProcessBytesRecorded(a.Buffer, a.BytesRecorded);
            capture.StartRecording();

            wasapiOut.Init(mediaFoundationReader);
            wasapiOut.Play();

            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.WriteLine();

            Console.ReadKey(true);

            wasapiOut.Stop();
            rttyDecoder.Stop();
            streamWriter?.Flush();
        }
    }
}