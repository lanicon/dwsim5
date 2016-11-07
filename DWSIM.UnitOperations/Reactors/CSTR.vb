'    CSTR Calculation Routines 
'    Copyright 2008 Daniel Wagner O. de Medeiros
'
'    This file is part of DWSIM.
'
'    DWSIM is free software: you can redistribute it and/or modify
'    it under the terms of the GNU General Public License as published by
'    the Free Software Foundation, either version 3 of the License, or
'    (at your option) any later version.
'
'    DWSIM is distributed in the hope that it will be useful,
'    but WITHOUT ANY WARRANTY; without even the implied warranty of
'    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
'    GNU General Public License for more details.
'
'    You should have received a copy of the GNU General Public License
'    along with DWSIM.  If not, see <http://www.gnu.org/licenses/>.

Imports DWSIM.DrawingTools.GraphicObjects
Imports DWSIM.Thermodynamics.BaseClasses
Imports Ciloci.Flee
Imports System.Math
Imports System.Linq
Imports DWSIM.Interfaces.Enums
Imports DWSIM.SharedClasses
Imports DWSIM.Thermodynamics.Streams
Imports DWSIM.Thermodynamics
Imports DWSIM.MathOps

Namespace Reactors

    <System.Serializable()> Public Class Reactor_CSTR

        Inherits Reactor

        Protected m_vol As Double
        Protected m_isotemp As Double

        Dim C0 As Dictionary(Of String, Double)
        Dim C As Dictionary(Of String, Double)
        Dim Ri As Dictionary(Of String, Double)
        Dim Kf, Kr As ArrayList
        Dim N00 As Dictionary(Of String, Double)
        Dim Rxi As New Dictionary(Of String, Double)
        Public RxiT As New Dictionary(Of String, Double)
        Public DHRi As New Dictionary(Of String, Double)

        Dim activeAL As Integer = 0

        <NonSerialized> <Xml.Serialization.XmlIgnore> Dim f As EditingForm_ReactorCSTR

        <System.NonSerialized()> Dim ims, ims0 As MaterialStream

        Public Property ResidenceTime As Double = 0.0#

        Private VolumeFraction As Double = 1.0#

        Public Property IsothermalTemperature() As Double
            Get
                Return m_isotemp
            End Get
            Set(ByVal value As Double)
                m_isotemp = value
            End Set
        End Property

        Public Property Volume() As Double
            Get
                Return m_vol
            End Get
            Set(ByVal value As Double)
                m_vol = value
            End Set
        End Property

        Public Property CatalystAmount As Double = 0.0#

        Public Sub New()

            MyBase.New()

        End Sub

        Public Sub New(ByVal name As String, ByVal description As String)

            MyBase.New()
            Me.ComponentName = name
            Me.ComponentDescription = description

            N00 = New Dictionary(Of String, Double)
            C0 = New Dictionary(Of String, Double)
            C = New Dictionary(Of String, Double)
            Ri = New Dictionary(Of String, Double)
            Rxi = New Dictionary(Of String, Double)
            DHRi = New Dictionary(Of String, Double)
            Kf = New ArrayList
            Kr = New ArrayList

        End Sub

        Public Function ODEFunc(ByVal x As Double, ByVal y As Double()) As Double()

            Dim conv As New SystemsOfUnits.Converter

            Dim i As Integer = 0
            Dim j As Integer = 0
            Dim scBC As Double = 0
            Dim BC As String = ""

            j = 0
            For Each s As String In N00.Keys
                C(s) = y(j) * ResidenceTime / (Volume * VolumeFraction)
                j = j + 1
            Next

            'conversion factors for different basis other than molar concentrations
            Dim convfactors As New Dictionary(Of String, Double)

            'loop through reactions
            Dim rxn As Reaction
            Dim ar As ArrayList = Me.ReactionsSequence(activeAL)

            i = 0
            Do
                'process reaction i
                rxn = FlowSheet.Reactions(ar(i))
                For Each sb As ReactionStoichBase In rxn.Components.Values
                    Ri(sb.CompName) = 0.0#
                Next
                i += 1
            Loop Until i = ar.Count

            i = 0
            Do

                'process reaction i
                rxn = FlowSheet.Reactions(ar(i))
                BC = rxn.BaseReactant
                scBC = rxn.Components(BC).StoichCoeff

                Dim T As Double = ims.Phases(0).Properties.temperature.GetValueOrDefault

                Dim rx As Double = 0.0#

                convfactors = Me.GetConvFactors(rxn, ims)

                If rxn.ReactionType = ReactionType.Kinetic Then

                    Dim kxf As Double = rxn.A_Forward * Exp(-rxn.E_Forward / (8.314 * T))
                    Dim kxr As Double = rxn.A_Reverse * Exp(-rxn.E_Reverse / (8.314 * T))

                    If T < rxn.Tmin Or T > rxn.Tmax Then
                        kxf = 0.0#
                        kxr = 0.0#
                    End If

                    Dim rxf As Double = 1.0#
                    Dim rxr As Double = 1.0#

                    'kinetic expression

                    For Each sb As ReactionStoichBase In rxn.Components.Values
                        rxf *= (C(sb.CompName) * convfactors(sb.CompName)) ^ sb.DirectOrder
                        rxr *= (C(sb.CompName) * convfactors(sb.CompName)) ^ sb.ReverseOrder
                    Next

                    rx = kxf * rxf - kxr * rxr
                    Rxi(rxn.ID) = SystemsOfUnits.Converter.ConvertToSI(rxn.VelUnit, rx)

                    Kf(i) = kxf
                    Kr(i) = kxr

                ElseIf rxn.ReactionType = ReactionType.Heterogeneous_Catalytic Then

                    Dim numval, denmval As Double

                    rxn.ExpContext = New Ciloci.Flee.ExpressionContext
                    rxn.ExpContext.Imports.AddType(GetType(System.Math))

                    rxn.ExpContext.Variables.Clear()
                    rxn.ExpContext.Variables.Add("T", ims.Phases(0).Properties.temperature.GetValueOrDefault)
                    rxn.ExpContext.Options.ParseCulture = Globalization.CultureInfo.InvariantCulture

                    Dim ir As Integer = 1
                    Dim ip As Integer = 1

                    For Each sb As ReactionStoichBase In rxn.Components.Values
                        If sb.StoichCoeff < 0 Then
                            rxn.ExpContext.Variables.Add("R" & ir.ToString, C(sb.CompName) * convfactors(sb.CompName))
                            ir += 1
                        ElseIf sb.StoichCoeff > 0 Then
                            rxn.ExpContext.Variables.Add("P" & ip.ToString, C(sb.CompName) * convfactors(sb.CompName))
                            ip += 1
                        End If
                    Next

                    rxn.Expr = rxn.ExpContext.CompileGeneric(Of Double)(rxn.RateEquationNumerator)

                    numval = rxn.Expr.Evaluate

                    rxn.Expr = rxn.ExpContext.CompileGeneric(Of Double)(rxn.RateEquationDenominator)

                    denmval = rxn.Expr.Evaluate

                    rx = SystemsOfUnits.Converter.ConvertToSI(rxn.VelUnit, numval / denmval)

                    Rxi(rxn.ID) = rx

                End If

                For Each sb As ReactionStoichBase In rxn.Components.Values

                    If rxn.ReactionType = ReactionType.Kinetic Then
                        Ri(sb.CompName) += Rxi(rxn.ID) * sb.StoichCoeff / rxn.Components(BC).StoichCoeff
                    ElseIf rxn.ReactionType = ReactionType.Heterogeneous_Catalytic Then
                        Ri(sb.CompName) += Rxi(rxn.ID) * sb.StoichCoeff / rxn.Components(BC).StoichCoeff * Me.CatalystAmount
                    End If

                Next

                i += 1

            Loop Until i = ar.Count

            Dim dy(Ri.Count - 1) As Double

            j = 0
            For Each kv As KeyValuePair(Of String, Double) In Ri
                dy(j) = -kv.Value * x
                j += 1
            Next

            FlowSheet.CheckStatus()

            Return dy

        End Function

        Public Overrides Sub Calculate(Optional ByVal args As Object = Nothing)

            N00 = New Dictionary(Of String, Double)
            C0 = New Dictionary(Of String, Double)
            C = New Dictionary(Of String, Double)
            Ri = New Dictionary(Of String, Double)
            DHRi = New Dictionary(Of String, Double)
            Kf = New ArrayList
            Kr = New ArrayList
            Rxi = New Dictionary(Of String, Double)

            m_conversions = New Dictionary(Of String, Double)
            m_componentconversions = New Dictionary(Of String, Double)

            Dim conv As New SystemsOfUnits.Converter
            Dim rxn As Reaction

            If Not Me.GraphicObject.InputConnectors(0).IsAttached Then
                Throw New Exception(FlowSheet.GetTranslatedString("Nohcorrentedematriac16"))
            ElseIf Not Me.GraphicObject.OutputConnectors(0).IsAttached Then
                Throw New Exception(FlowSheet.GetTranslatedString("Nohcorrentedematriac15"))
            ElseIf Not Me.GraphicObject.InputConnectors(1).IsAttached Then
                Throw New Exception(FlowSheet.GetTranslatedString("Nohcorrentedeenerg17"))
            End If

            Dim N0 As New Dictionary(Of String, Double)
            Dim N As New Dictionary(Of String, Double)
            Dim Cprev As New Dictionary(Of String, Double)
            N00.Clear()

            Dim i, ic, ec As Integer

            Dim scBC, DHr, Hr, Hr0, Hp, T, T0, P, P0, W, Qf, Q, m0 As Double
            Dim BC As String = ""
            Dim tmp As IFlashCalculationResult
            Dim maxXarr As New ArrayList

            ims0 = DirectCast(FlowSheet.SimulationObjects(Me.GraphicObject.InputConnectors(0).AttachedConnector.AttachedFrom.Name), MaterialStream).Clone

            Dim Tout0, Tout As Double

            Dim errt As Double = 1.0#, errc As Double = 1.0#

            ec = 0

            While errt > 0.01

                ims = DirectCast(FlowSheet.SimulationObjects(Me.GraphicObject.InputConnectors(0).AttachedConnector.AttachedFrom.Name), MaterialStream).Clone

                Ri.Clear()
                Rxi.Clear()
                DHRi.Clear()
                m_conversions.Clear()
                m_componentconversions.Clear()

                C0 = New Dictionary(Of String, Double)
                C = New Dictionary(Of String, Double)()

                DHr = 0.0#
                Hr = 0.0#
                Hp = 0.0#

                N0.Clear()
                N.Clear()
                'N00.Clear()

                Me.Reactions.Clear()
                Me.ReactionsSequence.Clear()
                Me.Conversions.Clear()
                Me.ComponentConversions.Clear()
                Me.DeltaQ = 0
                Me.DeltaT = 0

                'check active reactions (kinetic and heterogeneous only) in the reaction set
                'check if there are multiple reactions on different phases (unsupported)

                Dim rxp As PhaseName = PhaseName.Mixture

                For Each rxnsb As ReactionSetBase In FlowSheet.ReactionSets(Me.ReactionSetID).Reactions.Values
                    rxn = FlowSheet.Reactions(rxnsb.ReactionID)
                    If rxn.ReactionType = ReactionType.Kinetic And rxnsb.IsActive Then
                        Me.Reactions.Add(rxnsb.ReactionID)
                        If rxp = PhaseName.Mixture Then rxp = rxn.ReactionPhase
                        If rxp <> rxn.ReactionPhase Then
                            Throw New Exception(FlowSheet.GetTranslatedString("MultipleReactionPhasesNotSupported"))
                        End If
                    ElseIf rxn.ReactionType = ReactionType.Heterogeneous_Catalytic And rxnsb.IsActive Then
                        Me.Reactions.Add(rxnsb.ReactionID)
                        If rxp = PhaseName.Mixture Then rxp = rxn.ReactionPhase
                        If rxp <> rxn.ReactionPhase Then
                            Throw New Exception(FlowSheet.GetTranslatedString("MultipleReactionPhasesNotSupported"))
                        End If
                    End If
                Next

                W = ims.Phases(0).Properties.massflow.GetValueOrDefault
                Hr0 = ims.Phases(0).Properties.enthalpy.GetValueOrDefault * W
                Q = ims.Phases(0).Properties.volumetric_flow.GetValueOrDefault()

                PropertyPackage.CurrentMaterialStream = ims
                ims.SetPropertyPackage(PropertyPackage)
                ims.SetFlowsheet(Me.FlowSheet)
                ims.PreferredFlashAlgorithmTag = Me.PreferredFlashAlgorithmTag

                If Tout <> 0.0# Then
                    ims.Phases(0).Properties.temperature = Tout
                    ims.Calculate(True, True)
                End If

                'order reactions
                i = 0
                Dim maxrank As Integer = 0
                For Each rxnsb As ReactionSetBase In FlowSheet.ReactionSets(Me.ReactionSetID).Reactions.Values
                    If rxnsb.Rank > maxrank And Me.Reactions.Contains(rxnsb.ReactionID) Then maxrank = rxnsb.Rank
                Next

                'ordering of parallel reactions
                i = 0
                Dim arr As New ArrayList
                Do
                    arr = New ArrayList
                    For Each rxnsb As ReactionSetBase In FlowSheet.ReactionSets(Me.ReactionSetID).Reactions.Values
                        If rxnsb.Rank = i And Me.Reactions.Contains(rxnsb.ReactionID) Then arr.Add(rxnsb.ReactionID)
                    Next
                    If arr.Count > 0 Then Me.ReactionsSequence.Add(i, arr)
                    i = i + 1
                Loop Until i = maxrank + 1

                PropertyPackage.CurrentMaterialStream = ims

                T0 = ims.Phases(0).Properties.temperature.GetValueOrDefault
                P0 = ims.Phases(0).Properties.pressure.GetValueOrDefault

                'conversion factors for different basis other than molar concentrations
                Dim convfactors As New Dictionary(Of String, Double)

                RxiT.Clear()
                DHRi.Clear()

                ims.Phases(0).Properties.pressure = P0 - DeltaP.GetValueOrDefault

                If ReactorOperationMode = OperationMode.OutletTemperature Then
                    ims.Phases(0).Properties.temperature = OutletTemperature
                End If

                C = New Dictionary(Of String, Double)
                C0 = New Dictionary(Of String, Double)

                Kf = New ArrayList(Me.Reactions.Count)
                Kr = New ArrayList(Me.Reactions.Count)

                T = ims.Phases(0).Properties.temperature.GetValueOrDefault
                P = ims.Phases(0).Properties.pressure.GetValueOrDefault

                'Reactants Enthalpy (kJ/kg * kg/s = kW)
                Hr = ims.Phases(0).Properties.enthalpy.GetValueOrDefault * W

                'handle phase change
                Dim phasechange As Boolean = False

                'loop through reactions
                For Each ar As ArrayList In Me.ReactionsSequence.Values

                    DHr = 0

                    ic = 0

                    'converge concentrations
                    Do

                        i = 0
                        Do
                          
                            'process reaction i
                            rxn = FlowSheet.Reactions(ar(i))

                            If Me.Reactions.Count > 0 Then
                                Select Case FlowSheet.Reactions(Me.Reactions(0)).ReactionPhase
                                    Case PhaseName.Vapor
                                        Qf = ims.Phases(2).Properties.volumetric_flow.GetValueOrDefault()
                                    Case PhaseName.Liquid
                                        Qf = ims.Phases(3).Properties.volumetric_flow.GetValueOrDefault()
                                    Case PhaseName.Mixture
                                        Qf = ims.Phases(3).Properties.volumetric_flow.GetValueOrDefault() +
                                            ims.Phases(2).Properties.volumetric_flow.GetValueOrDefault()
                                End Select
                            End If

                            VolumeFraction = Qf / Q

                            ResidenceTime = Volume / Q

                            'initial mole flows
                            For Each sb As ReactionStoichBase In rxn.Components.Values

                                If phasechange Then
                                    Select Case rxn.ReactionPhase
                                        Case PhaseName.Liquid
                                            m0 = N00(sb.CompName)
                                            m0 -= ims0.Phases(2).Compounds(sb.CompName).MolarFlow.GetValueOrDefault
                                            m0 -= ims0.Phases(7).Compounds(sb.CompName).MolarFlow.GetValueOrDefault
                                        Case PhaseName.Vapor
                                            m0 = N00(sb.CompName)
                                            m0 -= ims0.Phases(3).Compounds(sb.CompName).MolarFlow.GetValueOrDefault
                                            m0 -= ims0.Phases(7).Compounds(sb.CompName).MolarFlow.GetValueOrDefault
                                        Case PhaseName.Mixture
                                            m0 = N00(sb.CompName)
                                    End Select
                                Else
                                    If ic = 0 Then
                                        Select Case rxn.ReactionPhase
                                            Case PhaseName.Liquid
                                                m0 = ims0.Phases(3).Compounds(sb.CompName).MolarFlow.GetValueOrDefault
                                            Case PhaseName.Vapor
                                                m0 = ims0.Phases(2).Compounds(sb.CompName).MolarFlow.GetValueOrDefault
                                            Case PhaseName.Mixture
                                                m0 = ims0.Phases(0).Compounds(sb.CompName).MolarFlow.GetValueOrDefault
                                        End Select
                                    Else
                                        Select Case rxn.ReactionPhase
                                            Case PhaseName.Liquid
                                                m0 = N00(sb.CompName)
                                            Case PhaseName.Vapor
                                                m0 = N00(sb.CompName)
                                            Case PhaseName.Mixture
                                                m0 = N00(sb.CompName)
                                        End Select
                                    End If
                                End If

                                If m0 = 0.0# Then m0 = 0.0000000001

                                If Not N00.ContainsKey(sb.CompName) Then N00.Add(sb.CompName, m0)

                                N0(sb.CompName) = m0
                                N(sb.CompName) = N0(sb.CompName)
                                C0(sb.CompName) = N0(sb.CompName) / Qf
                                
                            Next

                            Kf.Add(0.0#)
                            Kr.Add(0.0#)

                            i += 1

                        Loop Until i = ar.Count

                        Ri.Clear()
                        Rxi.Clear()

                        Me.activeAL = Me.ReactionsSequence.IndexOfValue(ar)

                        'solve ODEs

                        Dim vc(N.Count - 1), vc0(N.Count - 1), vcf(N.Count - 1) As Double
                        i = 0
                        For Each d As Double In N0.Values
                            vc(i) = d
                            vc0(i) = vc(i)
                            i = i + 1
                        Next

                        Dim odesolver = New DotNumerics.ODE.OdeImplicitRungeKutta5()
                        odesolver.InitializeODEs(AddressOf ODEFunc, N.Count)
                        odesolver.Solve(vc, 0.0#, 0.05 * Volume * VolumeFraction, Volume * VolumeFraction, Sub(x As Double, y As Double()) vc = y)

                        If Double.IsNaN(vc.Sum) Then Throw New Exception(FlowSheet.GetTranslatedString("PFRMassBalanceError"))

                        For Each kvp In C
                            Cprev(kvp.Key) = kvp.Value
                        Next

                        C.Clear()
                        i = 0
                        For Each sb As KeyValuePair(Of String, Double) In C0
                            C(sb.Key) = vc(i) * ResidenceTime / (Volume * VolumeFraction)
                            i = i + 1
                        Next

                        i = 0
                        Do

                            'process reaction i
                            rxn = FlowSheet.Reactions(ar(i))
                            BC = rxn.BaseReactant
                            scBC = rxn.Components(BC).StoichCoeff

                            For Each sb As ReactionStoichBase In rxn.Components.Values

                                ''comp. conversions
                                If Not Me.ComponentConversions.ContainsKey(sb.CompName) Then
                                    Me.ComponentConversions.Add(sb.CompName, 0)
                                End If

                            Next

                            i += 1

                        Loop Until i = ar.Count

                        i = 0
                        For Each sb As String In Me.ComponentConversions.Keys
                            N(sb) = vc(i)
                            i += 1
                        Next

                        DHRi.Clear()

                        i = 0
                        Do

                            'process reaction i
                            rxn = FlowSheet.Reactions(ar(i))

                            'Heat released (or absorbed) (kJ/s = kW) (Ideal Gas)
                            DHr = rxn.ReactionHeat * (N00(rxn.BaseReactant) - N(rxn.BaseReactant)) / 1000 * Rxi(rxn.ID) / Ri(rxn.BaseReactant)

                            DHRi.Add(rxn.ID, DHr)

                            i += 1

                        Loop Until i = ar.Count

                        'update mole flows/fractions
                        Dim Nsum As Double = 0

                        'compute new mole flows
                        'Nsum = ims.Phases(0).Properties.molarflow.GetValueOrDefault
                        For Each s2 As Compound In ims.Phases(0).Compounds.Values
                            If N.ContainsKey(s2.Name) Then
                                Nsum += N(s2.Name)
                            Else
                                Nsum += s2.MolarFlow.GetValueOrDefault
                            End If
                        Next
                        For Each s2 As Compound In ims.Phases(0).Compounds.Values
                            If N.ContainsKey(s2.Name) Then
                                s2.MoleFraction = N(s2.Name) / Nsum
                                s2.MolarFlow = N(s2.Name)
                            Else
                                s2.MoleFraction = ims.Phases(0).Compounds(s2.Name).MolarFlow.GetValueOrDefault / Nsum
                                s2.MolarFlow = ims.Phases(0).Compounds(s2.Name).MolarFlow.GetValueOrDefault
                            End If
                        Next

                        ims.Phases(0).Properties.molarflow = Nsum

                        Dim mmm As Double = 0
                        Dim mf As Double = 0

                        For Each s3 As Compound In ims.Phases(0).Compounds.Values
                            mmm += s3.MoleFraction.GetValueOrDefault * s3.ConstantProperties.Molar_Weight
                        Next

                        For Each s3 As Compound In ims.Phases(0).Compounds.Values
                            s3.MassFraction = s3.MoleFraction.GetValueOrDefault * s3.ConstantProperties.Molar_Weight / mmm
                            s3.MassFlow = s3.MassFraction.GetValueOrDefault * ims.Phases(0).Properties.massflow.GetValueOrDefault
                            mf += s3.MassFlow.GetValueOrDefault
                        Next

                        'do a flash calc (calculate final temperature/enthalpy)

                        Me.PropertyPackage.CurrentMaterialStream = ims

                        Select Case Me.ReactorOperationMode

                            Case OperationMode.Adiabatic

                                Me.DeltaQ = 0.0#

                                'Products Enthalpy (kJ/kg * kg/s = kW)
                                Hp = Hr0 - DHr

                                tmp = Me.PropertyPackage.CalculateEquilibrium2(FlashCalculationType.PressureEnthalpy, P, Hp / W, Tout)
                                Tout0 = Tout
                                Tout = tmp.CalculatedTemperature

                                errt = Abs(Tout - Tout0)

                                ims.Phases(0).Properties.temperature = Tout
                                ims.Phases(0).Properties.enthalpy = Hp / W
                                T = Tout

                                ims.SpecType = StreamSpec.Pressure_and_Enthalpy

                            Case OperationMode.Isothermic

                                errt = 1.0E-20

                                ims.SpecType = StreamSpec.Temperature_and_Pressure

                            Case OperationMode.OutletTemperature

                                errt = 1.0E-20

                                DeltaT = OutletTemperature - T0

                                ims.Phases(0).Properties.temperature = T0 + DeltaT

                                T = ims.Phases(0).Properties.temperature.GetValueOrDefault

                                ims.SpecType = StreamSpec.Temperature_and_Pressure

                        End Select

                        Dim v0, v, l0, l, s0, s As Double

                        v0 = ims.Phases(2).Properties.molarfraction.GetValueOrDefault()
                        l0 = ims.Phases(3).Properties.molarfraction.GetValueOrDefault()
                        s0 = ims.Phases(7).Properties.molarfraction.GetValueOrDefault()

                        ims.Calculate(True, True)

                        v = ims.Phases(2).Properties.molarfraction.GetValueOrDefault()
                        l = ims.Phases(3).Properties.molarfraction.GetValueOrDefault()
                        s = ims.Phases(7).Properties.molarfraction.GetValueOrDefault()

                        If Abs(v - v0) + Abs(l - l0) + Abs(s - s0) > 0.01 Then phasechange = True

                        errc = C.Values.ToArray.SubtractY(Cprev.Values.ToArray).AbsSqrSumY

                        ic += 1

                    Loop Until errc < 0.01 And ic > 2

                Next

                ec += 1

            End While

            ' comp. conversions
            For Each sb As Compound In ims.Phases(0).Compounds.Values
                If Me.ComponentConversions.ContainsKey(sb.Name) Then
                    Me.ComponentConversions(sb.Name) += (N00(sb.Name) - N(sb.Name)) / N00(sb.Name)
                End If
            Next

            RxiT.Clear()
            DHRi.Clear()

            For Each ar As ArrayList In Me.ReactionsSequence.Values

                i = 0
                Do

                    'process reaction i
                    rxn = FlowSheet.Reactions(ar(i))

                    RxiT.Add(rxn.ID, (N(rxn.BaseReactant) - N00(rxn.BaseReactant)) / rxn.Components(rxn.BaseReactant).StoichCoeff / 1000)
                    DHRi.Add(rxn.ID, rxn.ReactionHeat * RxiT(rxn.ID) * rxn.Components(rxn.BaseReactant).StoichCoeff / 1000)

                    i += 1

                Loop Until i = ar.Count

            Next

            If Me.ReactorOperationMode = OperationMode.Isothermic Then

                'Products Enthalpy (kJ/kg * kg/s = kW)
                Hp = ims.Phases(0).Properties.enthalpy.GetValueOrDefault * ims.Phases(0).Properties.massflow.GetValueOrDefault

                DeltaQ = DHr + Hp - Hr

                DeltaT = 0.0#

            ElseIf Me.ReactorOperationMode = OperationMode.OutletTemperature Then

                'Products Enthalpy (kJ/kg * kg/s = kW)
                Hp = ims.Phases(0).Properties.enthalpy.GetValueOrDefault * ims.Phases(0).Properties.massflow.GetValueOrDefault

                DeltaQ = DHr + Hp - Hr

                DeltaT = OutletTemperature - T0

            ElseIf ReactorOperationMode = OperationMode.Adiabatic Then

                DeltaT = Tout - T0

            End If

            Dim ms As MaterialStream
            Dim cp As ConnectionPoint
            Dim mtotal, wtotal As Double

            cp = Me.GraphicObject.OutputConnectors(0)
            If cp.IsAttached Then
                ms = FlowSheet.SimulationObjects(cp.AttachedConnector.AttachedTo.Name)
                With ms
                    .SpecType = ims.SpecType
                    .Phases(0).Properties.massflow = ims.Phases(0).Properties.massflow.GetValueOrDefault
                    .Phases(0).Properties.massfraction = 1
                    .Phases(0).Properties.temperature = ims.Phases(0).Properties.temperature.GetValueOrDefault
                    .Phases(0).Properties.pressure = ims.Phases(0).Properties.pressure.GetValueOrDefault
                    .Phases(0).Properties.enthalpy = ims.Phases(0).Properties.enthalpy.GetValueOrDefault
                    Dim comp As BaseClasses.Compound
                    mtotal = 0
                    wtotal = 0
                    For Each comp In .Phases(0).Compounds.Values
                        mtotal += ims.Phases(0).Compounds(comp.Name).MoleFraction.GetValueOrDefault
                        wtotal += ims.Phases(0).Compounds(comp.Name).MassFraction.GetValueOrDefault
                    Next
                    For Each comp In .Phases(0).Compounds.Values
                        comp.MoleFraction = ims.Phases(0).Compounds(comp.Name).MoleFraction.GetValueOrDefault / mtotal
                        comp.MassFraction = ims.Phases(0).Compounds(comp.Name).MassFraction.GetValueOrDefault / wtotal
                        comp.MassFlow = comp.MassFraction.GetValueOrDefault * .Phases(0).Properties.massflow.GetValueOrDefault
                        comp.MolarFlow = comp.MoleFraction.GetValueOrDefault * .Phases(0).Properties.molarflow.GetValueOrDefault
                    Next
                End With
            End If

            'Corrente de EnergyFlow - atualizar valor da potencia (kJ/s)
            Dim estr As Streams.EnergyStream = FlowSheet.SimulationObjects(Me.GraphicObject.InputConnectors(1).AttachedConnector.AttachedFrom.Name)
            With estr
                .EnergyFlow = Me.DeltaQ.GetValueOrDefault
                .GraphicObject.Calculated = True
            End With

        End Sub

        Public Overrides Sub DeCalculate()

            Dim j As Integer = 0

            Dim ms As MaterialStream
            Dim cp As ConnectionPoint

            cp = Me.GraphicObject.OutputConnectors(0)
            If cp.IsAttached Then
                ms = FlowSheet.SimulationObjects(cp.AttachedConnector.AttachedTo.Name)
                With ms
                    .Phases(0).Properties.temperature = Nothing
                    .Phases(0).Properties.pressure = Nothing
                    .Phases(0).Properties.enthalpy = Nothing
                    Dim comp As BaseClasses.Compound
                    j = 0
                    For Each comp In .Phases(0).Compounds.Values
                        comp.MoleFraction = 0
                        comp.MassFraction = 0
                        j += 1
                    Next
                    .Phases(0).Properties.massflow = Nothing
                    .Phases(0).Properties.massfraction = 1
                    .Phases(0).Properties.molarfraction = 1
                    .GraphicObject.Calculated = False
                End With
            End If

        End Sub

        Public Overrides Function GetPropertyValue(ByVal prop As String, Optional ByVal su As Interfaces.IUnitsOfMeasure = Nothing) As Object
            If su Is Nothing Then su = New SystemsOfUnits.SI
            Dim cv As New SystemsOfUnits.Converter
            Dim value As Double = 0
            Dim propidx As Integer = Convert.ToInt32(prop.Split("_")(2))

            Select Case propidx
                Case 0
                    value = SystemsOfUnits.Converter.ConvertFromSI(su.deltaP, Me.DeltaP.GetValueOrDefault)
                Case 1
                    value = SystemsOfUnits.Converter.ConvertFromSI(su.time, Me.ResidenceTime)
                Case 2
                    value = SystemsOfUnits.Converter.ConvertFromSI(su.volume, Me.Volume)
                Case 3
                    value = SystemsOfUnits.Converter.ConvertFromSI(su.deltaT, Me.DeltaT.GetValueOrDefault)
                Case 4
                    value = SystemsOfUnits.Converter.ConvertFromSI(su.heatflow, Me.DeltaQ.GetValueOrDefault)
            End Select

            Return value
        End Function

        Public Overloads Overrides Function GetProperties(ByVal proptype As Interfaces.Enums.PropertyType) As String()
            Dim i As Integer = 0
            Dim proplist As New ArrayList
            Select Case proptype
                Case PropertyType.RW
                    For i = 0 To 4
                        proplist.Add("PROP_CS_" + CStr(i))
                    Next
                Case PropertyType.WR
                    For i = 0 To 4
                        proplist.Add("PROP_CS_" + CStr(i))
                    Next
                Case PropertyType.ALL
                    For i = 0 To 4
                        proplist.Add("PROP_CS_" + CStr(i))
                    Next
            End Select
            Return proplist.ToArray(GetType(System.String))
            proplist = Nothing
        End Function

        Public Overrides Function SetPropertyValue(ByVal prop As String, ByVal propval As Object, Optional ByVal su As Interfaces.IUnitsOfMeasure = Nothing) As Boolean
            If su Is Nothing Then su = New SystemsOfUnits.SI
            Dim cv As New SystemsOfUnits.Converter
            Dim propidx As Integer = Convert.ToInt32(prop.Split("_")(2))

            Select Case propidx
                Case 0
                    Me.DeltaP = SystemsOfUnits.Converter.ConvertToSI(su.deltaP, propval)
                Case 1
                    Me.ResidenceTime = SystemsOfUnits.Converter.ConvertToSI(su.time, propval)
                Case 2
                    Me.Volume = SystemsOfUnits.Converter.ConvertToSI(su.volume, propval)
                Case 3
                    Me.DeltaT = SystemsOfUnits.Converter.ConvertToSI(su.deltaT, propval)
            End Select
            Return 1
        End Function

        Public Overrides Function GetPropertyUnit(ByVal prop As String, Optional ByVal su As Interfaces.IUnitsOfMeasure = Nothing) As String
            If su Is Nothing Then su = New SystemsOfUnits.SI
            Dim cv As New SystemsOfUnits.Converter
            Dim value As String = ""
            Dim propidx As Integer = Convert.ToInt32(prop.Split("_")(2))

            Select Case propidx
                Case 0
                    value = su.deltaP
                Case 1
                    value = su.time
                Case 2
                    value = su.volume
                Case 3
                    value = su.deltaT
                Case 4
                    value = su.heatflow
            End Select

            Return value
        End Function

        Public Overrides Sub DisplayEditForm()

            If f Is Nothing Then
                f = New EditingForm_ReactorCSTR With {.SimObject = Me}
                f.ShowHint = GlobalSettings.Settings.DefaultEditFormLocation
                Me.FlowSheet.DisplayForm(f)
            Else
                If f.IsDisposed Then
                    f = New EditingForm_ReactorCSTR With {.SimObject = Me}
                    f.ShowHint = GlobalSettings.Settings.DefaultEditFormLocation
                    Me.FlowSheet.DisplayForm(f)
                Else
                    f.Activate()
                End If
            End If

        End Sub

        Public Overrides Sub UpdateEditForm()
            If f IsNot Nothing Then
                If Not f.IsDisposed Then
                    f.UIThread(Sub() f.UpdateInfo())
                End If
            End If
        End Sub

        Public Overrides Function GetIconBitmap() As Object
            Return My.Resources.re_cstr_32
        End Function

        Public Overrides Function GetDisplayDescription() As String
            If GlobalSettings.Settings.CurrentCulture = "pt-BR" Then
                Return "Modelo de um CSTR, suporta reações Cinéticas e Catalíticas Heterogêneas"
            Else
                Return "CSTR model, supports Kinetic and HetCat reactions"
            End If
        End Function

        Public Overrides Function GetDisplayName() As String
            If GlobalSettings.Settings.CurrentCulture = "pt-BR" Then
                Return "Continous Stirred Tank Reactor (CSTR)"
            Else
                Return "Continous Stirred Tank Reactor (CSTR)"
            End If
        End Function

        Public Overrides Sub CloseEditForm()
            If f IsNot Nothing Then
                If Not f.IsDisposed Then
                    f.Close()
                    f = Nothing
                End If
            End If
        End Sub

    End Class

End Namespace


