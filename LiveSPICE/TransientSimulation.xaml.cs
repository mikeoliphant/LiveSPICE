﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SyMath;

namespace LiveSPICE
{
    /// <summary>
    /// Interaction logic for Simulation.xaml
    /// </summary>
    public partial class TransientSimulation : Window, INotifyPropertyChanged
    {
        protected int oversample = 4;
        public int Oversample
        {
            get { return oversample; }
            set { oversample = value; Build(); NotifyChanged("Oversample"); }
        }

        protected int iterations = 8;
        public int Iterations
        {
            get { return iterations; }
            set { iterations = value; NotifyChanged("Iterations"); }
        }

        protected Circuit.Quantity input = new Circuit.Quantity("V1[t]", Circuit.Units.V);
        public Circuit.Quantity Input
        {
            get { return input; }
            set { input = value; NotifyChanged("Input"); }
        }

        protected Circuit.Quantity output = new Circuit.Quantity("V[O1]", Circuit.Units.V);
        public Circuit.Quantity Output
        {
            get { return output; }
            set { output = value; NotifyChanged("Output"); }
        }
        
        protected Circuit.Circuit circuit = null;
        protected Circuit.Simulation simulation = null;
        protected Circuit.TransientSolution solution = null;

        protected Dictionary<SyMath.Expression, SyMath.Expression> componentVoltages;
        protected List<Probe> probes = new List<Probe>();
        protected Dictionary<SyMath.Expression, double> arguments = new Dictionary<SyMath.Expression, double>();

        public TransientSimulation(Circuit.Schematic Simulate)
        {
            InitializeComponent();
                        
            // Make a clone of the schematic so we can mess with it.
            Circuit.Schematic clone = Circuit.Schematic.Deserialize(Simulate.Serialize(), log);
            clone.Elements.ItemAdded += OnElementAdded;
            clone.Elements.ItemRemoved += OnElementRemoved;
            schematic.Schematic = new SimulationSchematic(clone);
            schematic.Schematic.SelectionChanged += OnProbeSelected;
            circuit = schematic.Schematic.Schematic.Build(log);

            // Find inputs and outputs to use as the default.
            IEnumerable<Circuit.Component> components = clone.Symbols.Select(i => i.Component);
            Input = components.OfType<Circuit.Input>().Select(i => Circuit.Component.DependentVariable(i.Name, Circuit.Component.t)).FirstOrDefault();
            Output = components.OfType<Circuit.Output>().Select(i => Circuit.Component.DependentVariable("V", i.Name)).FirstOrDefault();
            
            parameters.ParameterChanged += (o, e) => arguments[e.Changed.Name] = e.Value;

            audio.Callback = ProcessSamples;

            Unloaded += (s, e) => audio.Stop();
        }

        private void OnElementAdded(object sender, Circuit.ElementEventArgs e)
        {
            if (e.Element is Circuit.Symbol && ((Circuit.Symbol)e.Element).Component is Probe)
            {
                Probe probe = (Probe)((Circuit.Symbol)e.Element).Component;
                
                Pen p;
                switch (probe.Color)
                {
                    // These two need to be brighter than the normal colors.
                    case Circuit.EdgeType.Red: p = new Pen(new SolidColorBrush(Color.FromRgb(255, 50, 50)), 1.0); break;
                    case Circuit.EdgeType.Blue: p = new Pen(new SolidColorBrush(Color.FromRgb(20, 180, 255)), 1.0); break;
                    default: p = ElementControl.MapToPen(probe.Color); break;
                }
                probe.Signal = new Signal() { Name = probe.V.ToString(), Pen = p };
                oscilloscope.Display.Signals.Add(probe.Signal);

                e.Element.LayoutChanged += (x, y) => ConnectProbes();
            }
            ConnectProbes();
        }

        private void OnElementRemoved(object sender, Circuit.ElementEventArgs e)
        {
            if (e.Element is Circuit.Symbol && ((Circuit.Symbol)e.Element).Component is Probe)
            {
                Probe probe = (Probe)((Circuit.Symbol)e.Element).Component;

                oscilloscope.Display.Signals.Remove(probe.Signal);
            }
        }

        private void OnProbeSelected(object sender, EventArgs e)
        {
            IEnumerable<Circuit.Symbol> selected = SimulationSchematic.ProbesOf(schematic.Schematic.Selected);
            if (selected.Any())
                oscilloscope.Display.SelectedSignal = ((Probe)selected.First().Component).Signal;
        }

        public void ConnectProbes()
        {
            lock (probes)
            {
                probes.Clear();
                foreach (Probe i in ((SimulationSchematic)schematic.Schematic).Probes.Where(i => i.ConnectedTo != null))
                    probes.Add(i);
            }
        }

        private BackgroundWorker builder;
        protected void Build()
        {
            simulation = null;
            try
            {
                builder = new BackgroundWorker();
                builder.DoWork += (o, e) =>
                {
                    try
                    {
                        Circuit.Quantity h = new Circuit.Quantity(1 / (sampleRate * Oversample), Circuit.Units.s);
                        if (solution == null || solution.TimeStep != h)
                        {
                            solution = Circuit.TransientSolution.SolveCircuit(circuit, h, log);
                            arguments = solution.Parameters.ToDictionary(i => i.Name, i => 0.5);
                            Dispatcher.Invoke(() => parameters.UpdateControls(solution.Parameters));
                        }
                        simulation = new Circuit.LinqCompiledSimulation(solution, Oversample, log);
                    }
                    catch (System.Exception ex)
                    {
                        log.WriteLine(Circuit.MessageType.Error, ex.Message);
                    }
                };
                builder.RunWorkerAsync();
            }
            catch (System.Exception ex)
            {
                log.WriteLine(Circuit.MessageType.Error, ex.Message);
            }
        }

        private double sampleRate;
        private void ProcessSamples(double[] In, double[] Out, double SampleRate)
        {
            // If the sample rate changes, we need to rebuild the simulation.
            if (sampleRate != SampleRate)
            {
                sampleRate = SampleRate;
                Build();
            }

            // If there is no simulation, just zero the samples and return.
            if (simulation == null)
            {
                for (int i = 0; i < Out.Length; ++i)
                    Out[i] = 0.0;
                return;
            }

            try
            {
                lock (probes)
                {
                    // Build the signal list.
                    IEnumerable<KeyValuePair<SyMath.Expression, double[]>> signals = probes.Select(i => i.AllocBuffer(Out.Length));

                    if (Output.Value != null)
                        signals = signals.Append(new KeyValuePair<SyMath.Expression, double[]>(circuit.Evaluate(Output.Value), Out));

                    // Process the samples!
                    simulation.Run(Input, In, signals, arguments, Iterations);

                    // Show the samples on the oscilloscope.
                    oscilloscope.ProcessSignals(Out.Length, probes.Select(i => new KeyValuePair<Signal, double[]>(i.Signal, i.Buffer)), SampleRate);
                }
            }
            catch (Circuit.SimulationDiverged Ex)
            {
                // If the simulation diverged more than one second ago, reset it and hope it doesn't happen again.
                log.WriteLine(Circuit.MessageType.Error, Ex.Message);
                if ((double)Ex.At > SampleRate)
                    simulation.Reset();
                else
                    simulation = null;
            }
            catch (Exception ex)
            {
                // If there was a more serious error, kill the simulation so the user can fix it.
                log.WriteLine(Circuit.MessageType.Error, ex.Message);
                simulation = null;
            }
        }

        private void Simulate_Executed(object sender, ExecutedRoutedEventArgs e) { Build(); }

        private void Exit_Executed(object sender, ExecutedRoutedEventArgs e) { Close(); }

        // INotifyPropertyChanged.
        private void NotifyChanged(string p)
        {
            if (PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs(p));
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }
}
