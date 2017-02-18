﻿namespace Microsoft.ApplicationInsights.Extensibility.PerfCounterCollector.Implementation.QuickPulse
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Runtime.Serialization.Json;
    using Helpers;

    using Microsoft.ApplicationInsights.Extensibility.Filtering;
    using Microsoft.ApplicationInsights.Extensibility.Implementation.Tracing;
    using Microsoft.ManagementServices.RealTimeDataProcessing.QuickPulseService;

    /// <summary>
    /// Service client for QPS service.
    /// </summary>
    internal sealed class QuickPulseServiceClient : IQuickPulseServiceClient
    {
        private readonly string instanceName;

        private readonly string streamId;

        private readonly string machineName;

        private readonly string version;

        private readonly TimeSpan timeout = TimeSpan.FromSeconds(3);

        private readonly Clock timeProvider;

        private readonly bool isWebApp;

        private readonly DataContractJsonSerializer serializerDataPoint = new DataContractJsonSerializer(typeof(MonitoringDataPoint));

        private readonly DataContractJsonSerializer serializerDataPointArray = new DataContractJsonSerializer(typeof(MonitoringDataPoint[]));

        private readonly DataContractJsonSerializer deserializerServerResponse = new DataContractJsonSerializer(typeof(CollectionConfigurationInfo));

        public QuickPulseServiceClient(Uri serviceUri, string instanceName, string streamId, string machineName, string version, Clock timeProvider, bool isWebApp, TimeSpan? timeout = null)
        {
            this.ServiceUri = serviceUri;
            this.instanceName = instanceName;
            this.streamId = streamId;
            this.machineName = machineName;
            this.version = version;
            this.timeProvider = timeProvider;
            this.isWebApp = isWebApp;
            this.timeout = timeout ?? this.timeout;
        }

        public Uri ServiceUri { get; }

        public bool? Ping(
            string instrumentationKey,
            DateTimeOffset timestamp,
            string configurationETag,
            out CollectionConfigurationInfo configurationInfo)
        {
            var path = string.Format(CultureInfo.InvariantCulture, "ping?ikey={0}", Uri.EscapeUriString(instrumentationKey));
            HttpWebResponse response = this.SendRequest(
                WebRequestMethods.Http.Post,
                path,
                true,
                configurationETag,
                stream => this.WritePingData(timestamp, stream));
            
            if (response == null)
            {
                configurationInfo = null;
                return null;
            }

            return this.ProcessResponse(response, configurationETag, out configurationInfo);
        }

        public bool? SubmitSamples(
            IEnumerable<QuickPulseDataSample> samples,
            string instrumentationKey,
            string configurationETag,
            out CollectionConfigurationInfo configurationInfo,
            string[] collectionConfigurationErrors)
        {
            var path = string.Format(CultureInfo.InvariantCulture, "post?ikey={0}", Uri.EscapeUriString(instrumentationKey));
            HttpWebResponse response = this.SendRequest(
                WebRequestMethods.Http.Post,
                path,
                false,
                configurationETag,
                stream => this.WriteSamples(samples, instrumentationKey, stream, collectionConfigurationErrors));

            if (response == null)
            {
                configurationInfo = null;
                return null;
            }

            return this.ProcessResponse(response, configurationETag, out configurationInfo);
        }

        private bool? ProcessResponse(HttpWebResponse response, string configurationETag, out CollectionConfigurationInfo configurationInfo)
        {
            configurationInfo = null;

            bool isSubscribed;
            if (!bool.TryParse(response.GetResponseHeader(QuickPulseConstants.XMsQpsSubscribedHeaderName), out isSubscribed))
            {
                return null;
            }

            string latestConfigurationETag = response.GetResponseHeader(QuickPulseConstants.XMsQpsConfigurationETagHeaderName);

            if (isSubscribed && !string.Equals(latestConfigurationETag, configurationETag, StringComparison.Ordinal))
            {
                try
                {
                    using (var stream = response.GetResponseStream())
                    {
                        configurationInfo = this.deserializerServerResponse.ReadObject(stream) as CollectionConfigurationInfo;
                    }
                }
                catch (Exception e)
                {
                    // couldn't read or deserialize the response
                    QuickPulseEventSource.Log.ServiceCommunicationFailedEvent(e.ToInvariantString());
                }
            }

            return isSubscribed;
        }

        private static double Round(double value)
        {
            return Math.Round(value, 4, MidpointRounding.AwayFromZero);
        }

        private void WritePingData(DateTimeOffset timestamp, Stream stream)
        {
            var dataPoint = new MonitoringDataPoint
            {
                Version = this.version,
                InvariantVersion = MonitoringDataPoint.CurrentInvariantVersion,
                //InstrumentationKey = instrumentationKey, // ikey is currently set in query string parameter
                Instance = this.instanceName,
                StreamId = this.streamId,
                MachineName = this.machineName,
                Timestamp = timestamp.UtcDateTime,
                IsWebApp = this.isWebApp
            };

            this.serializerDataPoint.WriteObject(stream, dataPoint);
        }

        private void WriteSamples(IEnumerable<QuickPulseDataSample> samples, string instrumentationKey, Stream stream, string[] errors)
        {
            var monitoringPoints = new List<MonitoringDataPoint>();

            foreach (var sample in samples)
            {
                var metricPoints = new List<MetricPoint>();

                metricPoints.AddRange(CreateDefaultMetrics(sample));

                metricPoints.AddRange(sample.PerfCountersLookup.Select(counter => new MetricPoint { Name = counter.Key, Value = Round(counter.Value), Weight = 1 }));

                metricPoints.AddRange(CreateOperationalizedMetrics(sample));
                
                ITelemetryDocument[] documents = sample.TelemetryDocuments.ToArray();
                Array.Reverse(documents);

                ProcessCpuData[] topCpuProcesses =
                    sample.TopCpuData.Select(p => new ProcessCpuData() { ProcessName = p.Item1, CpuPercentage = p.Item2 }).ToArray();

                var dataPoint = new MonitoringDataPoint
                                    {
                                        Version = this.version,
                                        InvariantVersion = MonitoringDataPoint.CurrentInvariantVersion,
                                        InstrumentationKey = instrumentationKey,
                                        Instance = this.instanceName,
                                        StreamId = this.streamId,
                                        MachineName = this.machineName,
                                        Timestamp = sample.EndTimestamp.UtcDateTime,
                                        IsWebApp = this.isWebApp,
                                        Metrics = metricPoints.ToArray(),
                                        Documents = documents,
                                        TopCpuProcesses = topCpuProcesses.Length > 0 ? topCpuProcesses : null,
                                        TopCpuDataAccessDenied = sample.TopCpuDataAccessDenied,
                                        CollectionConfigurationErrors = errors
                                    };

                monitoringPoints.Add(dataPoint);
            }

            this.serializerDataPointArray.WriteObject(stream, monitoringPoints.ToArray());
        }

        private static IEnumerable<MetricPoint> CreateOperationalizedMetrics(QuickPulseDataSample sample)
        {
            var metrics = new List<MetricPoint>();

            foreach (AccumulatedValue metricAccumulatedValue in
                sample.CollectionConfigurationAccumulator.MetricAccumulators.Values)
            {
                try
                {
                    double[] accumulatedValues = metricAccumulatedValue.Value.ToArray();

                    MetricPoint metricPoint = new MetricPoint
                                                  {
                                                      Name = metricAccumulatedValue.MetricId,
                                                      Value =
                                                          OperationalizedMetric<int>.Aggregate(
                                                              accumulatedValues,
                                                              metricAccumulatedValue.AggregationType),
                                                      Weight = accumulatedValues.Length
                                                  };
                    metrics.Add(metricPoint);
                }
                catch (Exception e)
                {
                    // skip this metric
                    QuickPulseEventSource.Log.UnknownErrorEvent(e.ToString());
                }
            }

            return metrics;
        }

        private static IEnumerable<MetricPoint> CreateDefaultMetrics(QuickPulseDataSample sample)
        {
            return new[]
                       {
                           new MetricPoint { Name = @"\ApplicationInsights\Requests/Sec",
                               Value = Round(sample.AIRequestsPerSecond),
                               Weight = 1 },
                           new MetricPoint
                               {
                                   Name = @"\ApplicationInsights\Request Duration",
                                   Value = Round(sample.AIRequestDurationAveInMs),
                                   Weight = sample.AIRequests
                               },
                           new MetricPoint
                               {
                                   Name = @"\ApplicationInsights\Requests Failed/Sec",
                                   Value = Round(sample.AIRequestsFailedPerSecond),
                                   Weight = 1
                               },
                           new MetricPoint
                               {
                                   Name = @"\ApplicationInsights\Requests Succeeded/Sec",
                                   Value = Round(sample.AIRequestsSucceededPerSecond),
                                   Weight = 1
                               },
                           new MetricPoint
                               {
                                   Name = @"\ApplicationInsights\Dependency Calls/Sec",
                                   Value = Round(sample.AIDependencyCallsPerSecond),
                                   Weight = 1
                               },
                           new MetricPoint
                               {
                                   Name = @"\ApplicationInsights\Dependency Call Duration",
                                   Value = Round(sample.AIDependencyCallDurationAveInMs),
                                   Weight = sample.AIDependencyCalls
                               },
                           new MetricPoint
                               {
                                   Name = @"\ApplicationInsights\Dependency Calls Failed/Sec",
                                   Value = Round(sample.AIDependencyCallsFailedPerSecond),
                                   Weight = 1
                               },
                           new MetricPoint
                               {
                                   Name = @"\ApplicationInsights\Dependency Calls Succeeded/Sec",
                                   Value = Round(sample.AIDependencyCallsSucceededPerSecond),
                                   Weight = 1
                               },
                           new MetricPoint { Name = @"\ApplicationInsights\Exceptions/Sec",
                               Value = Round(sample.AIExceptionsPerSecond),
                               Weight = 1 }
                       };
        }

        private HttpWebResponse SendRequest(string httpVerb, string path, bool includeHeaders, string configurationETag, Action<Stream> onWriteBody)
        {
            var requestUri = string.Format(CultureInfo.InvariantCulture, "{0}/{1}", this.ServiceUri.AbsoluteUri.TrimEnd('/'), path.TrimStart('/'));

            try
            {
                var request = WebRequest.Create(requestUri) as HttpWebRequest;
                request.Method = httpVerb;
                request.Timeout = (int)this.timeout.TotalMilliseconds;
                request.Headers.Add(QuickPulseConstants.XMsQpsTransmissionTimeHeaderName, this.timeProvider.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                request.Headers.Add(QuickPulseConstants.XMsQpsConfigurationETagHeaderName, configurationETag);

                if (includeHeaders)
                {
                    request.Headers.Add(QuickPulseConstants.XMsQpsInstanceNameHeaderName, this.instanceName);
                    request.Headers.Add(QuickPulseConstants.XMsQpsStreamIdHeaderName, this.streamId);
                    request.Headers.Add(QuickPulseConstants.XMsQpsMachineNameHeaderName, this.machineName);
                }

                onWriteBody?.Invoke(request.GetRequestStream());

                var response = request.GetResponse() as HttpWebResponse;
                if (response != null)
                {
                    return response;
                }
            }
            catch (Exception e)
            {
                QuickPulseEventSource.Log.ServiceCommunicationFailedEvent(e.ToInvariantString());
            }

            return null;
        }
    }
}