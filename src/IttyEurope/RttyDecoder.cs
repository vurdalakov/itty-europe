namespace Vurdalakov.Rtty
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    internal class RttyDecoder
    {
        private readonly EventWaitHandle _exitWaitHandle = new AutoResetEvent(false);
        private readonly Int32 _sampleRate;
        private readonly Int32 _bytesPerSample;
        private readonly Int32 _numberOfChannels;
        private readonly Int32 _samplesPerBit;
        private readonly ConcurrentQueue<Double> _samples = new();
        private readonly StreamWriter _streamWriter;

        public RttyDecoder(Int32 sampleRate, Int32 bytesPerSample, Int32 numberOfChannels, StreamWriter streamWriter)
        {
            const Int32 Baud = 50;

            this._sampleRate = sampleRate;
            this._bytesPerSample = bytesPerSample;
            this._numberOfChannels = numberOfChannels;
            this._streamWriter = streamWriter;

            this._samplesPerBit = (Int32)Math.Round((Double)sampleRate / numberOfChannels / Baud);

            this._exitWaitHandle.Reset();
        }

        public void Start() => Task.Factory.StartNew(this.Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        public void Stop() => this._exitWaitHandle.Set();

        public void ProcessBytesRecorded(Byte[] bytes, Int32 bytesRecorded)
        {
            var sampleCount = bytesRecorded / this._bytesPerSample;

            var byteIndex = 0;
            for (var i = 0; i < sampleCount; i++)
            {
                var single = BitConverter.ToSingle(bytes, byteIndex);

                if (0 == single)
                {
                    return;
                }

                byteIndex += this._bytesPerSample;

                this._samples.Enqueue(single);
            }
        }

        private void Run()
        {
            while (!this.StopRequested() && this.ProcessSample())
            {
            }
        }

        private Int32 _lastBitCount = 0;
        private Int32 _lastBitValue = -1;

        private Boolean ProcessSample()
        {
            if (!this.TryGetSample(out var sample))
            {
                return false;
            }

            // bandpass filtering the sample
            var line0 = this.BandPassFilter0(sample);
            var line1 = this.BandPassFilter1(sample);

            // calculating the RMS of the two lines
            line0 *= line0;
            line1 *= line1;

            // inverting line 1
            line1 *= -1;

            // summing the two lines
            line0 += line1;

            // lowpass filtering the summed line
            line0 = this.LowPassFilter(line0);

            // detecting the bit value
            var bit = line0 > 0 ? 1 : 0;

            // detecting the bit value change
            if (bit != this._lastBitValue)
            {
                var bitCount = Math.Round(1.0D * this._lastBitCount / this._samplesPerBit / 2, 1);

                this.ProcessBitChange(this._lastBitValue, bitCount);

                this._lastBitValue = bit;
                this._lastBitCount = 0;
            }
            else
            {
                this._lastBitCount++;
            }

            return true;
        }

        private Int32 _totalBitsReceived = 0;
        private Boolean _startBitReceived = false;
        private Int32 _bitCountReceived = 0;
        private Int32 _byteReceived = 0;

        private void ProcessBitChange(Int32 bitValue, Double bitCount)
        {
            var bitCountInteger = (Int32)Math.Round(bitCount * 10);
            var stopBitReceived = 5 == (bitCountInteger % 10);
            bitCountInteger /= 10;

            if (this._totalBitsReceived < 100)
            {
                this._totalBitsReceived += bitCountInteger;
                return;
            }

            if (stopBitReceived && (bitValue != 1))
            {
                throw new Exception("Zero stop bit received");
            }

            if (this._startBitReceived)
            {
                if ((1 == bitValue) && (this._bitCountReceived < 8))
                {
                    this._byteReceived |= ((1 << bitCountInteger) - 1) << this._bitCountReceived;
                }

                this._bitCountReceived += bitCountInteger;

                if (stopBitReceived)
                {
                    if (7 == this._bitCountReceived)
                    {
                        this.ProcessByteReceived(this._byteReceived);
                    }

                    this._byteReceived = 0;
                    this._bitCountReceived = 0;
                }
            }
            else if (stopBitReceived)
            {
                this._startBitReceived = true;
            }
        }

        private static readonly Char[] RttyLetters = ['¤', 'E', '\n', 'A', ' ', 'S',  'I', 'U', '\r', 'D', 'R', 'J',  'N', 'F', 'C', 'K', 'T', 'Z', 'L', 'W', 'H', 'Y', 'P', 'Q', 'O', 'B', 'G', '¤', 'M', 'X', 'V', '¤'];
        private static readonly Char[] RttySymbols = ['¤', '3', '\n', '-', ' ', '\'', '8', '7', '\r', '#', '4', '\b', ',', '!', ':', '(', '5', '+', ')', '2', '£', '6', '0', '1', '9', '?', '&', '¤', '.', '/', '=', '¤'];
        private Boolean _isLettersMode = true;

        private void ProcessByteReceived(Int32 byteReceived)
        {
            byteReceived = (byteReceived >> 1) & 0x1F;

            switch (byteReceived)
            {
                case 31:
                    this._isLettersMode = true;
                    break;
                case 27:
                    this._isLettersMode = false;
                    break;
                default:
                    if (this._isLettersMode)
                    {
                        this.ProcessCharReceived(RttyLetters[byteReceived]);
                    }
                    else
                    {
                        this.ProcessCharReceived(RttySymbols[byteReceived]);
                    }
                    break;
            }
        }

        private void ProcessCharReceived(Char c)
        {
            Console.Write(c);

            if ((this._streamWriter != null) && !this.StopRequested())
            {
                this._streamWriter.Write(c);

                if ('\n' == c)
                {
                    this._streamWriter.Flush();
                }
            }
        }

        private Boolean StopRequested() => this._exitWaitHandle.WaitOne(0);

        private Boolean TryGetSample(out Double sample)
        {
            while (!this.StopRequested())
            {
                if (this._samples.TryDequeue(out sample))
                {
                    return true;
                }

                Thread.Sleep(10);
            }

            sample = Double.NaN;
            return false;
        }

#pragma warning disable SA1407 // Arithmetic expressions should declare precedence

        private readonly Double[] _xvBP0 = new Double[5];
        private readonly Double[] _yvBP0 = new Double[5];

        private Double BandPassFilter0(Double sample)
        {
            // Order 2 Butterworth, freqs: 1225-1325 Hz (MARK 1275 Hz)
            this._xvBP0[0] = this._xvBP0[1];
            this._xvBP0[1] = this._xvBP0[2];
            this._xvBP0[2] = this._xvBP0[3];
            this._xvBP0[3] = this._xvBP0[4];
            this._xvBP0[4] = sample / 2.356080518e+04;
            this._yvBP0[0] = this._yvBP0[1];
            this._yvBP0[1] = this._yvBP0[2];
            this._yvBP0[2] = this._yvBP0[3];
            this._yvBP0[3] = this._yvBP0[4];
            this._yvBP0[4] = this._xvBP0[0] + this._xvBP0[4] - 2 * this._xvBP0[2] + -0.9816582826 * this._yvBP0[0] + 3.8900752312 * this._yvBP0[1] + -5.8354295192 * this._yvBP0[2] + 3.9262497236 * this._yvBP0[3];
            return this._yvBP0[4];
        }

        private readonly Double[] _xvBP1 = new Double[5];
        private readonly Double[] _yvBP1 = new Double[5];

        private Double BandPassFilter1(Double sample)
        {
            // Order 2 Butterworth, freqs: 1395-1495 Hz (SPACE 1445 Hz)
            this._xvBP1[0] = this._xvBP1[1];
            this._xvBP1[1] = this._xvBP1[2];
            this._xvBP1[2] = this._xvBP1[3];
            this._xvBP1[3] = this._xvBP1[4];
            this._xvBP1[4] = sample / 2.356080588e+04;
            this._yvBP1[0] = this._yvBP1[1];
            this._yvBP1[1] = this._yvBP1[2];
            this._yvBP1[2] = this._yvBP1[3];
            this._yvBP1[3] = this._yvBP1[4];
            this._yvBP1[4] = this._xvBP1[0] + this._xvBP1[4] - 2 * this._xvBP1[2] + -0.9816582826 * this._yvBP1[0] + 3.8745300933 * this._yvBP1[1] + -5.8046895783 * this._yvBP1[2] + 3.9105600286 * this._yvBP1[3];
            return this._yvBP1[4];
        }

        private readonly Double[] _xvLP = new Double[3];
        private readonly Double[] _yvLP = new Double[3];

        private Double LowPassFilter(Double sample)
        {
            // Order 2 Butterworth, freq: 50 Hz
            this._xvLP[0] = this._xvLP[1];
            this._xvLP[1] = this._xvLP[2];
            this._xvLP[2] = sample / 9.381008646e+04;
            this._yvLP[0] = this._yvLP[1];
            this._yvLP[1] = this._yvLP[2];
            this._yvLP[2] = this._xvLP[0] + this._xvLP[2] + 2 * this._xvLP[1] + -0.9907866988 * this._yvLP[0] + 1.9907440595 * this._yvLP[1];
            return this._yvLP[2];
        }

#pragma warning restore SA1407 // Arithmetic expressions should declare precedence
    }
}