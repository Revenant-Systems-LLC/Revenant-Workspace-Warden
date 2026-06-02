using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace RevenantWorkspaceWarden;

public class AudioTranscriber : IDisposable
{
    private WhisperFactory? _whisperFactory;
    private WhisperProcessor? _processor;
    private WaveInEvent? _waveIn;
    private bool _isRecording;
    private Task? _processingTask;
    private CancellationTokenSource? _cts;

    private readonly BlockingCollection<float[]> _audioQueue = new();
    private readonly string _modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ggml-medium.en.bin");
    private readonly string _transcriptPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevenantWorkspaceWarden", "lesson_transcript.md");

    public Action<string>? OnTranscriptReady;

    public async Task InitializeAsync()
    {
        if (_whisperFactory != null) return; // Cached factory loaded in memory!

        if (!File.Exists(_modelPath))
        {
            using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(GgmlType.MediumEn);
            using var fileWriter = File.OpenWrite(_modelPath);
            await modelStream.CopyToAsync(fileWriter);
        }

        _whisperFactory = WhisperFactory.FromPath(_modelPath);
        _processor = _whisperFactory.CreateBuilder()
            .WithLanguage("en")
            .Build();
    }

    public void Start()
    {
        if (_isRecording) return;
        _isRecording = true;
        _cts = new CancellationTokenSource();

        var dir = Path.GetDirectoryName(_transcriptPath);
        if (dir != null) Directory.CreateDirectory(dir);

        if (!File.Exists(_transcriptPath))
        {
            File.WriteAllText(_transcriptPath, "# Lesson Transcript\n\n");
        }

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1)
        };
        
        _waveIn.DataAvailable += WaveIn_DataAvailable;
        _waveIn.StartRecording();

        _processingTask = Task.Run(() => ProcessAudioQueue(_cts.Token));
    }

    private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            var floats = new float[e.BytesRecorded / 2];
            for (int i = 0; i < floats.Length; i++)
            {
                short val = BitConverter.ToInt16(e.Buffer, i * 2);
                floats[i] = val / 32768f;
            }
            _audioQueue.Add(floats);
        }
        catch
        {
            // Ignore corrupted buffer exceptions to keep thread alive
        }
    }

    private async Task ProcessAudioQueue(CancellationToken token)
    {
        if (_processor == null) return;
        
        List<float> buffer = new();
        
        while (!token.IsCancellationRequested)
        {
            if (_audioQueue.TryTake(out var chunk, 100))
            {
                buffer.AddRange(chunk);
            }

            // Process every ~5 seconds of audio (16000 samples * 5)
            if (buffer.Count >= 16000 * 5)
            {
                var floatsToProcess = buffer.ToArray();
                buffer.Clear();
                
                try
                {
                    await foreach (var segment in _processor.ProcessAsync(floatsToProcess, token))
                    {
                        var text = segment.Text.Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            using (var fs = new FileStream(_transcriptPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                            using (var sw = new StreamWriter(fs))
                            {
                                sw.WriteLine(text);
                            }
                            OnTranscriptReady?.Invoke(text);
                        }
                    }
                }
                catch
                {
                    // Ignore transient processing errors
                }
            }
        }
    }

    public void Stop()
    {
        if (!_isRecording) return;
        _isRecording = false;
        
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;
        
        _cts?.Cancel();
    }

    public void Dispose()
    {
        Stop();
        _processor?.Dispose();
        _whisperFactory?.Dispose();
    }
}
