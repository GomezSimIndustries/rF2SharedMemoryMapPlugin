/*
rF2SMMonitor is visual debugger for rF2 Shared Memory Plugin.

MainForm implementation, contains main loop and render calls.

Author: The Iron Wolf (vleonavicius@hotmail.com)
Website: thecrewchief.org
*/
using rF2SMMonitor.rFactor2Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using static rF2SMMonitor.rFactor2Constants;

namespace rF2SMMonitor
{
  public partial class MainForm : Form
  {
    // Connection fields
    private const int CONNECTION_RETRY_INTERVAL_MS = 1000;
    private const int DISCONNECTED_CHECK_INTERVAL_MS = 15000;
    private const float DEGREES_IN_RADIAN = 57.2957795f;
    private const int LIGHT_MODE_REFRESH_MS = 500;

    public static bool useStockCarRulesPlugin = false;

    System.Windows.Forms.Timer connectTimer = new System.Windows.Forms.Timer();
    System.Windows.Forms.Timer disconnectTimer = new System.Windows.Forms.Timer();
    bool connected = false;

    // Read buffers:
    MappedBuffer<rF2Telemetry> telemetryBuffer = new MappedBuffer<rF2Telemetry>(MM_TELEMETRY_FILE_NAME, true /*partial*/, true /*skipUnchanged*/);
    MappedBuffer<rF2Scoring> scoringBuffer = new MappedBuffer<rF2Scoring>(MM_SCORING_FILE_NAME, true /*partial*/, true /*skipUnchanged*/);
    MappedBuffer<rF2Rules> rulesBuffer = new MappedBuffer<rF2Rules>(MM_RULES_FILE_NAME, true /*partial*/, true /*skipUnchanged*/);
    MappedBuffer<rF2ForceFeedback> forceFeedbackBuffer = new MappedBuffer<rF2ForceFeedback>(MM_FORCE_FEEDBACK_FILE_NAME, false /*partial*/, false /*skipUnchanged*/);
    MappedBuffer<rF2Graphics> graphicsBuffer = new MappedBuffer<rF2Graphics>(MM_GRAPHICS_FILE_NAME, false /*partial*/, false /*skipUnchanged*/);
    MappedBuffer<rF2PitInfo> pitInfoBuffer = new MappedBuffer<rF2PitInfo>(MM_PITINFO_FILE_NAME, false /*partial*/, true /*skipUnchanged*/);
    MappedBuffer<rF2Weather> weatherBuffer = new MappedBuffer<rF2Weather>(MM_WEATHER_FILE_NAME, false /*partial*/, true /*skipUnchanged*/);
    MappedBuffer<rF2Extended> extendedBuffer = new MappedBuffer<rF2Extended>(MM_EXTENDED_FILE_NAME, false /*partial*/, true /*skipUnchanged*/);

    // Write buffers:
    MappedBuffer<rF2HWControl> hwControlBuffer = new MappedBuffer<rF2HWControl>(MM_HWCONTROL_FILE_NAME);
    MappedBuffer<rF2WeatherControl> weatherControlBuffer = new MappedBuffer<rF2WeatherControl>(MM_WEATHER_CONTROL_FILE_NAME);
    MappedBuffer<rF2RulesControl> rulesControlBuffer = new MappedBuffer<rF2RulesControl>(MM_RULES_CONTROL_FILE_NAME);
    MappedBuffer<rF2PluginControl> pluginControlBuffer = new MappedBuffer<rF2PluginControl>(MM_PLUGIN_CONTROL_FILE_NAME);

    // Marshalled views:
    rF2Telemetry telemetry;
    rF2Scoring scoring;
    rF2Rules rules;
    rF2ForceFeedback forceFeedback;
    rF2Graphics graphics;
    rF2PitInfo pitInfo;
    rF2Weather weather;
    rF2Extended extended;

    // Marashalled output views:
    rF2HWControl hwControl;
    rF2WeatherControl weatherControl;
    rF2RulesControl rulesControl;
    rF2PluginControl pluginControl;

    // Track rF2 transitions.
    TransitionTracker tracker = new TransitionTracker();

    // Config
    IniFile config = new IniFile();
    float scale = 2.0f;
    float xOffset = 0.0f;
    float yOffset = 0.0f;
    int focusVehicle = 0;
    bool centerOnVehicle = true;
    bool rotateAroundVehicle = true;
    bool logPhaseAndState = true;
    bool logDamage = true;
    bool logTiming = true;
    bool logRules = true;
    bool logLightMode = false;
    bool enablePitInputs = false;

    // Capture of the max FFB force.
    double maxFFBValue = 0.0;

    // Last applied value for the rain intensity.
    double rainIntensityRequested = 0.0;

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeMessage
    {
      public IntPtr Handle;
      public uint Message;
      public IntPtr WParameter;
      public IntPtr LParameter;
      public uint Time;
      public Point Location;
    }

    [DllImport("user32.dll")]
    public static extern int PeekMessage(out NativeMessage message, IntPtr window, uint filterMin, uint filterMax, uint remove);

    public MainForm()
    {
      InitializeComponent();

      DoubleBuffered = true;
      StartPosition = FormStartPosition.Manual;
      Location = new Point(0, 0);

      EnableControls(false);
      scaleTextBox.KeyDown += TextBox_KeyDown;
      scaleTextBox.LostFocus += ScaleTextBox_LostFocus;
      xOffsetTextBox.KeyDown += TextBox_KeyDown;
      xOffsetTextBox.LostFocus += XOffsetTextBox_LostFocus;
      yOffsetTextBox.KeyDown += TextBox_KeyDown;
      yOffsetTextBox.LostFocus += YOffsetTextBox_LostFocus;
      focusVehTextBox.KeyDown += TextBox_KeyDown;
      focusVehTextBox.LostFocus += FocusVehTextBox_LostFocus;
      setAsOriginCheckBox.CheckedChanged += SetAsOriginCheckBox_CheckedChanged;
      rotateAroundCheckBox.CheckedChanged += RotateAroundCheckBox_CheckedChanged;
      logPhaseAndStateCheckBox.CheckedChanged += CheckBoxLogPhaseAndState_CheckedChanged;
      logDamageCheckBox.CheckedChanged += CheckBoxLogDamage_CheckedChanged;
      logTimingCheckBox.CheckedChanged += CheckBoxLogTiming_CheckedChanged;
      logRulesCheckBox.CheckedChanged += CheckBoxLogRules_CheckedChanged;
      lightModeCheckBox.CheckedChanged += CheckBoxLightMode_CheckedChanged;
      enablePitInputsCheckBox.CheckedChanged += CheckBoxEnablePitInputs_CheckedChanged;
      MouseWheel += MainForm_MouseWheel;

      rainIntensityTextBox.LostFocus += RainIntensityTextBox_LostFocus;
      rainIntensityTextBox.Text = "0.0";
      applyRainIntensityButton.Click += ApplyRainIntensityButton_Click;

      LoadConfig();
      connectTimer.Interval = CONNECTION_RETRY_INTERVAL_MS;
      connectTimer.Tick += ConnectTimer_Tick;
      disconnectTimer.Interval = DISCONNECTED_CHECK_INTERVAL_MS;
      disconnectTimer.Tick += DisconnectTimer_Tick;
      connectTimer.Start();
      disconnectTimer.Start();

      view.BorderStyle = BorderStyle.Fixed3D;
      view.Paint += View_Paint;
      MouseClick += MainForm_MouseClick;
      view.MouseClick += MainForm_MouseClick;

      Application.Idle += HandleApplicationIdle;
    }

    private void ApplyRainIntensityButton_Click(object sender, EventArgs e)
    {
      if (!connected
        || extended.mWeatherControlInputEnabled == 0)
        return;

      weatherControl.mVersionUpdateBegin = weatherControl.mVersionUpdateEnd = weatherControl.mVersionUpdateBegin + 1;

      // First, copy current state into control buffer.
      // This is not a deep copy. Values in weather buffer wwill change.  If that is not desired, deep copy needs to be performed.
      weatherControl.mWeatherInfo = weather.mWeatherInfo;

      weatherControl.mWeatherInfo.mET += 5.0;  // Apply in 5 seconds.
      // Apply requested rain intensity.
      weatherControl.mWeatherInfo.mRaining[4] = rainIntensityRequested;

      weatherControlBuffer.PutMappedData(ref weatherControl);
      applyRainIntensityButton.Enabled = false;
    }

    private void CheckBoxEnablePitInputs_CheckedChanged(object sender, EventArgs e)
    {
      enablePitInputs = enablePitInputsCheckBox.Checked;
      config.Write("enablePitInputs", enablePitInputs ? "1" : "0");
    }

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(Keys vKey);

    private DateTime nextKeyHandlingTime = DateTime.MinValue;
    private void ProcessKeys()
    {
      if (!connected
        || !enablePitInputs
        || extended.mHWControlInputEnabled == 0)
        return;

      var now = DateTime.Now;
      if (now < nextKeyHandlingTime)
        return;

      nextKeyHandlingTime = now + TimeSpan.FromMilliseconds(100);

      byte[] commandStr = null;
      var fRetVal = 1.0;

      if (GetAsyncKeyState(Keys.U) != 0)
        commandStr = Encoding.Default.GetBytes("PitMenuIncrementValue");
      else if (GetAsyncKeyState(Keys.Y) != 0)
        commandStr = Encoding.Default.GetBytes("PitMenuDecrementValue");
      else if (GetAsyncKeyState(Keys.P) != 0)
        commandStr = Encoding.Default.GetBytes("PitMenuUp");
      else if (GetAsyncKeyState(Keys.O) != 0)
        commandStr = Encoding.Default.GetBytes("PitMenuDown");
      // rough sample for rule input buffer.
      /*else if (MainForm.GetAsyncKeyState(Keys.T) != 0)
      {
        if (this.extended.mRulesControlInputEnabled == 0)
          return;

        this.rulesControl.mVersionUpdateBegin = this.rulesControl.mVersionUpdateEnd = this.rulesControl.mVersionUpdateBegin + 1;

        // First, copy current state into control buffer.
        // This is not a deep copy. Values in rules buffer wwill change.  If that is not desired, deep copy needs to be performed.
        this.rulesControl.mTrackRules = this.rules.mTrackRules;
        this.rulesControl.mActions = this.rules.mActions;
        this.rulesControl.mParticipants = this.rules.mParticipants;

        this.rulesControl.mTrackRules.mMessage = new byte[96];
        var msg = Encoding.Default.GetBytes("Hello!");  // should be visible in LSI during FCY?
        for (int i = 0; i < msg.Length; ++i)
          this.rulesControl.mTrackRules.mMessage[i] = msg[i];

        this.rulesControlBuffer.PutMappedData(ref this.rulesControl);
      }*/

      SendPitMenuCmd(commandStr, fRetVal);
    }

    private void SendPitMenuCmd(byte[] commandStr, double fRetVal)
    {
      if (commandStr != null)
      {
        hwControl.mVersionUpdateBegin = hwControl.mVersionUpdateEnd = hwControl.mVersionUpdateBegin + 1;

        hwControl.mControlName = new byte[MAX_HWCONTROL_NAME_LEN];
        for (int i = 0; i < commandStr.Length; ++i)
          hwControl.mControlName[i] = commandStr[i];

        hwControl.mfRetVal = fRetVal;

        hwControlBuffer.PutMappedData(ref hwControl);
      }
    }
    private void CheckBoxLogRules_CheckedChanged(object sender, EventArgs e)
    {
      logRules = logRulesCheckBox.Checked;
      config.Write("logRules", logRules ? "1" : "0");
    }

    private void CheckBoxLightMode_CheckedChanged(object sender, EventArgs e)
    {
      logLightMode = lightModeCheckBox.Checked;

      // Disable/enable rendering options
      globalGroupBox.Enabled = !logLightMode;
      groupBoxFocus.Enabled = !logLightMode;

      config.Write("logLightMode", logLightMode ? "1" : "0");
    }

    private void CheckBoxLogDamage_CheckedChanged(object sender, EventArgs e)
    {
      logDamage = logDamageCheckBox.Checked;
      config.Write("logDamage", logDamage ? "1" : "0");
    }

    private void CheckBoxLogTiming_CheckedChanged(object sender, EventArgs e)
    {
      logTiming = logTimingCheckBox.Checked;
      config.Write("logTiming", logTiming ? "1" : "0");
    }

    private void CheckBoxLogPhaseAndState_CheckedChanged(object sender, EventArgs e)
    {
      logPhaseAndState = logPhaseAndStateCheckBox.Checked;
      config.Write("logPhaseAndState", logPhaseAndState ? "1" : "0");
    }

    private void MainForm_MouseClick(object sender, MouseEventArgs e)
    {
      if (e.Button == MouseButtons.Right)
      {
        delayAccMicroseconds = 0;
        numDelayUpdates = 0;

        telemetryBuffer.ClearStats();
        scoringBuffer.ClearStats();
        extendedBuffer.ClearStats();
        rulesBuffer.ClearStats();

        // No stats for FFB buffer (single value buffer).

        maxFFBValue = 0.0;
      }
    }

    private void RainIntensityTextBox_LostFocus(object sender, EventArgs e)
    {
      var result = 0.0;
      if (double.TryParse(rainIntensityTextBox.Text, out result)
        && result >= 0.0 && result <= 1.0)
      {
        if (rainIntensityRequested != result)
          applyRainIntensityButton.Enabled = true;

        rainIntensityRequested = result;
      }

      rainIntensityTextBox.Text = rainIntensityRequested.ToString("0.0");
    }

    private void YOffsetTextBox_LostFocus(object sender, EventArgs e)
    {
      float result = 0.0f;
      if (float.TryParse(yOffsetTextBox.Text, out result))
      {
        yOffset = result;
        config.Write("yOffset", yOffset.ToString());
      }
      else
        yOffsetTextBox.Text = yOffset.ToString();
    }

    private void XOffsetTextBox_LostFocus(object sender, EventArgs e)
    {
      float result = 0.0f;
      if (float.TryParse(xOffsetTextBox.Text, out result))
      {
        xOffset = result;
        config.Write("xOffset", xOffset.ToString());
      }
      else
        xOffsetTextBox.Text = xOffset.ToString();

    }

    private void MainForm_MouseWheel(object sender, MouseEventArgs e)
    {
      float step = 0.5f;
      if (scale < 5.0f)
        step = 0.25f;
      else if (scale < 2.0f)
        step = 0.1f;
      else if (scale < 1.0f)
        step = 0.05f;

      if (e.Delta > 0)
        scale += step;
      else if (e.Delta < 0)
        scale -= step;

      if (scale <= 0.0f)
        scale = 0.05f;

      config.Write("scale", scale.ToString());
      scaleTextBox.Text = scale.ToString();
    }

    private void RotateAroundCheckBox_CheckedChanged(object sender, EventArgs e)
    {
      rotateAroundVehicle = rotateAroundCheckBox.Checked;
      config.Write("rotateAroundVehicle", rotateAroundVehicle ? "1" : "0");
    }

    private void SetAsOriginCheckBox_CheckedChanged(object sender, EventArgs e)
    {
      centerOnVehicle = setAsOriginCheckBox.Checked;
      rotateAroundCheckBox.Enabled = setAsOriginCheckBox.Checked;
      config.Write("centerOnVehicle", centerOnVehicle ? "1" : "0");
    }

    private void FocusVehTextBox_LostFocus(object sender, EventArgs e)
    {
      int result = 0;
      if (int.TryParse(focusVehTextBox.Text, out result) && result >= 0)
      {
        focusVehicle = result;
        config.Write("focusVehicle", focusVehicle.ToString());
      }
      else
        focusVehTextBox.Text = focusVehTextBox.ToString();
    }

    private void ScaleTextBox_LostFocus(object sender, EventArgs e)
    {
      float result = 0.0f;
      if (float.TryParse(scaleTextBox.Text, out result))
      {
        scale = Math.Max(result, 0.05f);
        config.Write("scale", scale.ToString());
      }
      else
        scaleTextBox.Text = scale.ToString();
    }

    private void TextBox_KeyDown(object sender, KeyEventArgs e)
    {
      if (e.KeyCode == Keys.Enter)
        view.Focus();
    }
    protected override void Dispose(bool disposing)
    {
      if (disposing && (components != null))
        components.Dispose();

      if (disposing)
        Disconnect();

      base.Dispose(disposing);
    }

    // Amazing loop implementation by Josh Petrie from:
    // http://gamedev.stackexchange.com/questions/67651/what-is-the-standard-c-windows-forms-game-loop
    bool IsApplicationIdle()
    {
      NativeMessage result;
      return PeekMessage(out result, IntPtr.Zero, (uint)0, (uint)0, (uint)0) == 0;
    }

    void HandleApplicationIdle(object sender, EventArgs e)
    {
      while (IsApplicationIdle())
      {
        try
        {
          MainUpdate();

          if (WindowState == FormWindowState.Minimized)
          {
            // being lazy lazy lazy.
            tracker.TrackPhase(ref scoring, ref telemetry, ref extended, null, logPhaseAndState);
            tracker.TrackDamage(ref scoring, ref telemetry, ref extended, null, logDamage);
            tracker.TrackTimings(ref scoring, ref telemetry, ref rules, ref extended, null, logTiming);
            tracker.TrackRules(ref scoring, ref telemetry, ref rules, ref extended, null, logRules);
          }
          else
          {
            MainRender();
          }

          ProcessKeys();

          if (logLightMode)
            Thread.Sleep(LIGHT_MODE_REFRESH_MS);
        }
        catch (Exception)
        {
          Disconnect();
        }
      }
    }

    long delayAccMicroseconds = 0;
    long numDelayUpdates = 0;
    float avgDelayMicroseconds = 0.0f;
    void MainUpdate()
    {
      if (!connected)
        return;

      try
      {
        var watch = Stopwatch.StartNew();

        extendedBuffer.GetMappedData(ref extended);
        scoringBuffer.GetMappedData(ref scoring);
        telemetryBuffer.GetMappedData(ref telemetry);
        rulesBuffer.GetMappedData(ref rules);
        forceFeedbackBuffer.GetMappedDataUnsynchronized(ref forceFeedback);
        graphicsBuffer.GetMappedDataUnsynchronized(ref graphics);
        pitInfoBuffer.GetMappedData(ref pitInfo);
        weatherBuffer.GetMappedData(ref weather);

        watch.Stop();
        var microseconds = watch.ElapsedTicks * 1000000 / Stopwatch.Frequency;
        delayAccMicroseconds += microseconds;
        ++numDelayUpdates;

        if (numDelayUpdates == 0)
        {
          numDelayUpdates = 1;
          delayAccMicroseconds = microseconds;
        }

        avgDelayMicroseconds = (float)delayAccMicroseconds / numDelayUpdates;
      }
      catch (Exception)
      {
        Disconnect();
      }
    }

    void MainRender()
    {
      view.Refresh();
    }

    int framesAvg = 20;
    int frame = 0;
    int fps = 0;
    Stopwatch fpsStopWatch = new Stopwatch();
    private void UpdateFPS()
    {
      if (frame > framesAvg)
      {
        fpsStopWatch.Stop();
        var tsSinceLastRender = fpsStopWatch.Elapsed;
        fps = tsSinceLastRender.Milliseconds > 0 ? (1000 * framesAvg) / tsSinceLastRender.Milliseconds : 0;
        fpsStopWatch.Restart();
        frame = 0;
      }
      else
        ++frame;
    }

    private static string GetStringFromBytes(byte[] bytes)
    {
      if (bytes == null)
        return "";

      var nullIdx = Array.IndexOf(bytes, (byte)0);

      return nullIdx >= 0
        ? Encoding.Default.GetString(bytes, 0, nullIdx)
        : Encoding.Default.GetString(bytes);
    }

    // Corrdinate conversion:
    // rF2 +x = screen +x
    // rF2 +z = screen -z
    // rF2 +yaw = screen -yaw
    // If I don't flip z, the projection will look from below.
    void View_Paint(object sender, PaintEventArgs e)
    {
      var g = e.Graphics;

      tracker.TrackPhase(ref scoring, ref telemetry, ref extended, g, logPhaseAndState);
      tracker.TrackDamage(ref scoring, ref telemetry, ref extended, g, logDamage);
      tracker.TrackTimings(ref scoring, ref telemetry, ref rules, ref extended, g, logTiming);
      tracker.TrackRules(ref scoring, ref telemetry, ref rules, ref extended, g, logRules);

      UpdateFPS();

      if (!connected)
      {
        var brush = new SolidBrush(Color.Black);
        g.DrawString("Not connected.", SystemFonts.DefaultFont, brush, 3.0f, 3.0f);

        if (logLightMode)
          return;
      }
      else
      {
        var brush = new SolidBrush(Color.Green);

        var currX = 3.0f;
        var currY = 3.0f;
        float yStep = SystemFonts.DefaultFont.Height;
        var gameStateText = new StringBuilder();

        // Capture FFB stats:
        maxFFBValue = Math.Max(Math.Abs(forceFeedback.mForceValue), maxFFBValue);

        gameStateText.Append(
          $"Plugin Version:    Expected: 3.7.15.0 64bit   Actual: {GetStringFromBytes(extended.mVersion)}"
          + $"{(extended.is64bit == 1 ? " 64bit" : " 32bit")}"
          + $"{(extended.mSCRPluginEnabled == 1 ? "    SCR Plugin enabled" : "")}"
          + $"{(extended.mDirectMemoryAccessEnabled == 1 ? "    DMA enabled" : "")}"
          + $"{(extended.mHWControlInputEnabled == 1 ? "    HWCI enabled" : "")}"
          + $"{(extended.mWeatherControlInputEnabled == 1 ? "    WCI enabled" : "")}"
          + $"{(extended.mRulesControlInputEnabled == 1 ? "    RCI enabled" : "")}"
          + $"{(extended.mPluginControlInputEnabled == 1 ? "    PCI enabled" : "")}"
          + $"    UBM: {extended.mUnsubscribedBuffersMask}"
          + $"    FPS: {fps}"
          + $"    FFB Curr: {forceFeedback.mForceValue:N3} Max: {maxFFBValue:N3}");

        // Draw header
        g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, brush, currX, currY);

        gameStateText.Clear();

        // Build map of mID -> telemetry.mVehicles[i].
        // They are typically matching values, however, we need to handle online cases and dropped vehicles (mID can be reused).
        var idsToTelIndices = new Dictionary<long, int>();
        for (int i = 0; i < telemetry.mNumVehicles; ++i)
        {
          if (!idsToTelIndices.ContainsKey(telemetry.mVehicles[i].mID))
            idsToTelIndices.Add(telemetry.mVehicles[i].mID, i);
        }

        var playerVehScoring = GetPlayerScoring(ref scoring);

        var scoringPlrId = playerVehScoring.mID;
        var playerVeh = new rF2VehicleTelemetry();
        int resolvedPlayerIdx = -1;  // We're fine here with unitialized vehicle telemetry..
        if (idsToTelIndices.ContainsKey(scoringPlrId))
        {
          resolvedPlayerIdx = idsToTelIndices[scoringPlrId];
          playerVeh = telemetry.mVehicles[resolvedPlayerIdx];
        }

        // Figure out prev session end player mID
        var playerSessionEndInfo = new rF2VehScoringCapture();
        for (int i = 0; i < extended.mSessionTransitionCapture.mNumScoringVehicles; ++i)
        {
          var veh = extended.mSessionTransitionCapture.mScoringVehicles[i];
          if (veh.mIsPlayer == 1)
            playerSessionEndInfo = veh;
        }

        gameStateText.Append(
          "mElapsedTime:\n"
          + "mCurrentET:\n"
          + "mElapsedTime-mCurrentET:\n"
          + "mDetlaTime:\n"
          + "mInvulnerable:\n"
          + "mVehicleName:\n"
          + "mTrackName:\n"
          + "mLapStartET:\n"
          + "mLapDist:\n"
          + "mEndET:\n"
          + "mPlayerName:\n"
          + "mPlrFileName:\n\n"
          + "Session Started:\n"
          + "Sess. End Session:\n"
          + "Sess. End Phase:\n"
          + "Sess. End Place:\n"
          + "Sess. End Finish:\n"
          + "Display msg capture:\n"
          );

        // Col 1 labels
        g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, brush, currX, currY += yStep);

        gameStateText.Clear();

        gameStateText.Append(
                $"{playerVeh.mElapsedTime:N3}\n"
                + $"{scoring.mScoringInfo.mCurrentET:N3}\n"
                + $"{(playerVeh.mElapsedTime - scoring.mScoringInfo.mCurrentET):N3}\n"
                + $"{playerVeh.mDeltaTime:N3}\n"
                + (extended.mPhysics.mInvulnerable == 0 ? "off" : "on") + "\n"
                + $"{GetStringFromBytes(playerVeh.mVehicleName)}\n"
                + $"{GetStringFromBytes(playerVeh.mTrackName)}\n"
                + $"{playerVeh.mLapStartET:N3}\n"
                + $"{scoring.mScoringInfo.mLapDist:N3}\n"
                + (scoring.mScoringInfo.mEndET < 0.0 ? "Unknown" : scoring.mScoringInfo.mEndET.ToString("N3")) + "\n"
                + $"{GetStringFromBytes(scoring.mScoringInfo.mPlayerName)}\n"
                + $"{GetStringFromBytes(scoring.mScoringInfo.mPlrFileName)}\n\n"
                + $"{extended.mSessionStarted != 0}\n"
                + $"{TransitionTracker.GetSessionString(extended.mSessionTransitionCapture.mSession)}\n"
                + $"{(rF2GamePhase)extended.mSessionTransitionCapture.mGamePhase}\n"
                + $"{playerSessionEndInfo.mPlace}\n"
                + $"{(rF2FinishStatus)playerSessionEndInfo.mFinishStatus}\n"
                + $"{GetStringFromBytes(extended.mDisplayedMessageUpdateCapture)}\n"
                );

        // Col1 values
        g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, Brushes.Purple, currX + 145, currY);

        // Print buffer stats.
        gameStateText.Clear();
        gameStateText.Append(
          "Telemetry:\n"
          + "Scoring:\n"
          + "Rules:\n"
          + "Extended:\n"
          + "Pit Info:\n"
          + "Weather:\n"
          + "Avg read:");

        g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, Brushes.Black, 1500, 570);

        gameStateText.Clear();
        gameStateText.Append(
          telemetryBuffer.GetStats() + '\n'
          + scoringBuffer.GetStats() + '\n'
          + rulesBuffer.GetStats() + '\n'
          + pitInfoBuffer.GetStats() + '\n'
          + weatherBuffer.GetStats() + '\n'
          + extendedBuffer.GetStats() + '\n'
          + avgDelayMicroseconds.ToString("0.000") + " microseconds");

        g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, Brushes.Black, 1560, 570);

        if (extended.mDirectMemoryAccessEnabled == 1)
        {
          gameStateText.Clear();
          gameStateText.Append(
            "Status:\n"
            + "Last MC msg:\n"
            + "Pit Speed Limit:\n"
            + "Last LSI Phase:\n"
            + "Last LSI Pit:\n"
            + "Last LSI Order:\n"
            + "Last SCR Instr.:\n"
            );

          g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, Brushes.Purple, 1500, 660);

          gameStateText.Clear();
          gameStateText.Append(
            GetStringFromBytes(extended.mStatusMessage) + '\n'
            + GetStringFromBytes(extended.mLastHistoryMessage) + '\n'
            + (int)(extended.mCurrentPitSpeedLimit * 3.6f + 0.5f) + "kph\n"
            + GetStringFromBytes(extended.mLSIPhaseMessage) + '\n'
            + GetStringFromBytes(extended.mLSIPitStateMessage) + '\n'
            + GetStringFromBytes(extended.mLSIOrderInstructionMessage) + '\n'
            + GetStringFromBytes(extended.mLSIRulesInstructionMessage) + '\n'
            );

          g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, Brushes.Purple, 1580, 660);

          gameStateText.Clear();
          gameStateText.Append(
            "updated: " + extended.mTicksStatusMessageUpdated + '\n'
            + "updated: " + extended.mTicksLastHistoryMessageUpdated + '\n'
            + '\n'
            + "updated: " + extended.mTicksLSIPhaseMessageUpdated + '\n'
            + "updated: " + extended.mTicksLSIPitStateMessageUpdated + '\n'
            + "updated: " + extended.mTicksLSIOrderInstructionMessageUpdated + '\n'
            + "updated: " + extended.mTicksLSIRulesInstructionMessageUpdated + '\n');

          g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, Brushes.Purple, 1800, 660);
        }

        if ((extended.mUnsubscribedBuffersMask & (long)SubscribedBuffer.PitInfo) == 0)
        {
          // Print pit info:
          gameStateText.Clear();

          gameStateText.Append(
            "PI Cat Index:\n"
            + "PI Cat Name:\n"
            + "PI Choice Index:\n"
            + "PI Choice String:\n"
            + "PI Num Choices:\n"
            );

          g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, Brushes.Orange, 1500, 750);

          gameStateText.Clear();
          var catName = GetStringFromBytes(pitInfo.mPitMneu.mCategoryName);
          var choiceStr = GetStringFromBytes(pitInfo.mPitMneu.mChoiceString);

          gameStateText.Append(
            pitInfo.mPitMneu.mCategoryIndex + "\n"
            + (string.IsNullOrWhiteSpace(catName) ? "<empty>" : catName) + "\n"
            + pitInfo.mPitMneu.mChoiceIndex + "\n"
            + (string.IsNullOrWhiteSpace(choiceStr) ? "<empty>" : choiceStr) + "\n"
            + pitInfo.mPitMneu.mNumChoices + "\n"
            );

          g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, Brushes.Orange, 1600, 750);
        }

        if (scoring.mScoringInfo.mNumVehicles == 0
          || resolvedPlayerIdx == -1)  // We need telemetry for stats below.
          return;

        gameStateText.Clear();

        gameStateText.Append(
          "mTimeIntoLap:\n"
          + "mEstimatedLapTime:\n"
          + "mTimeBehindNext:\n"
          + "mTimeBehindLeader:\n"
          + "mPitGroup:\n"
          + "mLapDist(Plr):\n"
          + "mLapDist(Est):\n"
          + "yaw:\n"
          + "pitch:\n"
          + "roll:\n"
          + "speed:\n");

        // Col 2 labels
        g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, brush, currX += 275, currY);
        gameStateText.Clear();


        // Calculate derivatives:
        var yaw = Math.Atan2(playerVeh.mOri[RowZ].x, playerVeh.mOri[RowZ].z);

        var pitch = Math.Atan2(-playerVeh.mOri[RowY].z,
          Math.Sqrt(playerVeh.mOri[RowX].z * playerVeh.mOri[RowX].z + playerVeh.mOri[RowZ].z * playerVeh.mOri[RowZ].z));

        var roll = Math.Atan2(playerVeh.mOri[RowY].x,
          Math.Sqrt(playerVeh.mOri[RowX].x * playerVeh.mOri[RowX].x + playerVeh.mOri[RowZ].x * playerVeh.mOri[RowZ].x));

        var speed = Math.Sqrt((playerVeh.mLocalVel.x * playerVeh.mLocalVel.x)
          + (playerVeh.mLocalVel.y * playerVeh.mLocalVel.y)
          + (playerVeh.mLocalVel.z * playerVeh.mLocalVel.z));

        // Estimate lapdist
        // See how much ahead telemetry is ahead of scoring update
        var delta = playerVeh.mElapsedTime - scoring.mScoringInfo.mCurrentET;
        var lapDistEstimated = playerVehScoring.mLapDist;
        if (delta > 0.0)
        {
          var localZAccelEstimated = playerVehScoring.mLocalAccel.z * delta;
          var localZVelEstimated = playerVehScoring.mLocalVel.z + localZAccelEstimated;

          lapDistEstimated = playerVehScoring.mLapDist - localZVelEstimated * delta;
        }

        gameStateText.Append(
          $"{playerVehScoring.mTimeIntoLap:N3}\n"
          + $"{playerVehScoring.mEstimatedLapTime:N3}\n"
          + $"{playerVehScoring.mTimeBehindNext:N3}\n"
          + $"{playerVehScoring.mTimeBehindLeader:N3}\n"
          + $"{GetStringFromBytes(playerVehScoring.mPitGroup)}\n"
          + $"{playerVehScoring.mLapDist:N3}\n"
          + $"{lapDistEstimated:N3}\n"
          + $"{yaw:N3}\n"
          + $"{pitch:N3}\n"
          + $"{roll:N3}\n"
          + string.Format("{0:n3} m/s {1:n4} km/h\n", speed, speed * 3.6));

        // Col2 values
        g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, Brushes.Purple, currX + 120, currY);

        if (logLightMode)
            return;

        // Branch of UI choice: origin center or car# center
        // Fix rotation on car of choice or no.
        // Draw axes
        // Scale will be parameter, scale applied last on render to zoom.
        float scale = this.scale;

        var xVeh = (float)playerVeh.mPos.x;
        var zVeh = (float)playerVeh.mPos.z;
        var yawVeh = yaw;

        // View center
        var xScrOrigin = view.Width / 2.0f;
        var yScrOrigin = view.Height / 2.0f;
        if (!centerOnVehicle)
        {
          // Set world origin.
          g.TranslateTransform(xScrOrigin, yScrOrigin);
          RenderOrientationAxis(g);
          g.ScaleTransform(scale, scale);

          RenderCar(g, xVeh, -zVeh, -(float)yawVeh, Brushes.Green);

          for (int i = 0; i < telemetry.mNumVehicles; ++i)
          {
            if (i == resolvedPlayerIdx)
              continue;

            var veh = telemetry.mVehicles[i];
            var thisYaw = Math.Atan2(veh.mOri[2].x, veh.mOri[2].z);
            RenderCar(g,
              (float)veh.mPos.x,
              -(float)veh.mPos.z,
              -(float)thisYaw, Brushes.Red);
          }
        }
        else
        {
          g.TranslateTransform(xScrOrigin, yScrOrigin);

          if (rotateAroundVehicle)
            g.RotateTransform(180.0f + (float)yawVeh * DEGREES_IN_RADIAN);

          RenderOrientationAxis(g);
          g.ScaleTransform(scale, scale);
          g.TranslateTransform(-xVeh, zVeh);

          RenderCar(g, xVeh, -zVeh, -(float)yawVeh, Brushes.Green);

          for (int i = 0; i < telemetry.mNumVehicles; ++i)
          {
            if (i == resolvedPlayerIdx)
              continue;

            var veh = telemetry.mVehicles[i];
            var thisYaw = Math.Atan2(veh.mOri[2].x, veh.mOri[2].z);
            RenderCar(g,
              (float)veh.mPos.x,
              -(float)veh.mPos.z,
              -(float)thisYaw, Brushes.Red);
          }
        }
      }
    }

    public static rF2VehicleScoring GetPlayerScoring(ref rF2Scoring scoring)
    {
      var playerVehScoring = new rF2VehicleScoring();
      for (int i = 0; i < scoring.mScoringInfo.mNumVehicles; ++i)
      {
        var vehicle = scoring.mVehicles[i];
        switch ((rF2Control)vehicle.mControl)
        {
          case rF2Control.AI:
          case rF2Control.Player:
          case rF2Control.Remote:
            if (vehicle.mIsPlayer == 1)
              playerVehScoring = vehicle;

            break;

          default:
            continue;
        }

        if (playerVehScoring.mIsPlayer == 1)
          break;
      }

      return playerVehScoring;
    }


    // Length
    // 174.6in (4,435mm)
    // 175.6in (4,460mm) (Z06, ZR1)
    // Width
    // 72.6in (1,844mm)
    // 75.9in (1,928mm) (Z06, ZR1)
    /*PointF[] carPoly =
    {
        new PointF(0.922f, 2.217f),
        new PointF(0.922f, -1.4f),
        new PointF(1.3f, -1.4f),
        new PointF(0.0f, -2.217f),
        new PointF(-1.3f, -1.4f),
        new PointF(-0.922f, -1.4f),
        new PointF(-0.922f, 2.217f),
      };*/

    PointF[] carPoly =
    {
      new PointF(-0.922f, -2.217f),
      new PointF(-0.922f, 1.4f),
      new PointF(-1.3f, 1.4f),
      new PointF(0.0f, 2.217f),
      new PointF(1.3f, 1.4f),
      new PointF(0.922f, 1.4f),
      new PointF(0.922f, -2.217f),
    };

    private void RenderCar(Graphics g, float x, float y, float yaw, Brush brush)
    {
      var state = g.Save();

      g.TranslateTransform(x, y);

      g.RotateTransform(yaw * DEGREES_IN_RADIAN);

      g.FillPolygon(brush, carPoly);

      g.Restore(state);
    }

    static float arrowSide = 10.0f;
    PointF[] arrowHead =
    {
      new PointF(-arrowSide / 2.0f, -arrowSide / 2.0f),
      new PointF(0.0f, arrowSide / 2.0f),
      new PointF(arrowSide / 2.0f, -arrowSide / 2.0f)
    };

    private void RenderOrientationAxis(Graphics g)
    {

      float length = 1000.0f;
      float arrowDistX = view.Width / 2.0f - 10.0f;
      float arrowDistY = view.Height / 2.0f - 10.0f;

      // X (x screen) axis
      g.DrawLine(Pens.Red, -length, 0.0f, length, 0.0f);
      var state = g.Save();
      g.TranslateTransform(rotateAroundVehicle ? arrowDistY : arrowDistX, 0.0f);
      g.RotateTransform(-90.0f);
      g.FillPolygon(Brushes.Red, arrowHead);
      g.RotateTransform(90.0f);
      g.DrawString("x+", SystemFonts.DefaultFont, Brushes.Red, -10.0f, 10.0f);
      g.Restore(state);

      state = g.Save();
      // Z (y screen) axis
      g.DrawLine(Pens.Blue, 0.0f, -length, 0.0f, length);
      g.TranslateTransform(0.0f, -arrowDistY);
      g.RotateTransform(180.0f);
      g.FillPolygon(Brushes.Blue, arrowHead);
      g.DrawString("z+", SystemFonts.DefaultFont, Brushes.Blue, 10.0f, -10.0f);

      g.Restore(state);
    }

    private void ConnectTimer_Tick(object sender, EventArgs e)
    {
      if (!connected)
      {
        try
        {
          // Extended buffer is the last one constructed, so it is an indicator RF2SM is ready.
          extendedBuffer.Connect();

          telemetryBuffer.Connect();
          scoringBuffer.Connect();
          rulesBuffer.Connect();
          forceFeedbackBuffer.Connect();
          graphicsBuffer.Connect();
          pitInfoBuffer.Connect();
          weatherBuffer.Connect();

          hwControlBuffer.Connect();
          hwControlBuffer.GetMappedData(ref hwControl);
          hwControl.mLayoutVersion = MM_HWCONTROL_LAYOUT_VERSION;

          weatherControlBuffer.Connect();
          weatherControlBuffer.GetMappedData(ref weatherControl);
          weatherControl.mLayoutVersion = MM_WEATHER_CONTROL_LAYOUT_VERSION;

          rulesControlBuffer.Connect();
          rulesControlBuffer.GetMappedData(ref rulesControl);
          rulesControl.mLayoutVersion = MM_RULES_CONTROL_LAYOUT_VERSION;

          pluginControlBuffer.Connect();
          pluginControlBuffer.GetMappedData(ref pluginControl);
          pluginControl.mLayoutVersion = MM_PLUGIN_CONTROL_LAYOUT_VERSION;

          // Scoring cannot be enabled on demand.
          pluginControl.mRequestEnableBuffersMask = /*(int)SubscribedBuffer.Scoring | */(int)SubscribedBuffer.Telemetry | (int)SubscribedBuffer.Rules
            | (int)SubscribedBuffer.ForceFeedback | (int)SubscribedBuffer.Graphics | (int)SubscribedBuffer.Weather | (int)SubscribedBuffer.PitInfo;
          pluginControl.mRequestHWControlInput = 1;
          pluginControl.mRequestRulesControlInput = 1;
          pluginControl.mRequestWeatherControlInput = 1;
          pluginControl.mVersionUpdateBegin = pluginControl.mVersionUpdateEnd = pluginControl.mVersionUpdateBegin + 1;
          pluginControlBuffer.PutMappedData(ref pluginControl);

          connected = true;

          EnableControls(true);
        }
        catch (Exception)
        {
          Disconnect();
        }
      }
    }

    private void DisconnectTimer_Tick(object sender, EventArgs e)
    {
      if (!connected)
        return;

      try
      {
        // Alternatively, I could release resources and try re-acquiring them immidiately.
        var processes = Process.GetProcessesByName(RFACTOR2_PROCESS_NAME);
        if (processes.Length == 0)
          Disconnect();
      }
      catch (Exception)
      {
        Disconnect();
      }
    }

    private void Disconnect()
    {
      extendedBuffer.Disconnect();
      scoringBuffer.Disconnect();
      rulesBuffer.Disconnect();
      telemetryBuffer.Disconnect();
      forceFeedbackBuffer.Disconnect();
      pitInfoBuffer.Disconnect();
      weatherBuffer.Disconnect();
      graphicsBuffer.Disconnect();

      hwControlBuffer.Disconnect();
      weatherControlBuffer.Disconnect();
      rulesControlBuffer.Disconnect();
      pluginControlBuffer.Disconnect();

      connected = false;

      EnableControls(false);
    }

    void EnableControls(bool enable)
    {
      globalGroupBox.Enabled = enable;
      groupBoxFocus.Enabled = enable;
      loggingGroupBox.Enabled = enable;
      inputsGroupBox.Enabled = enable;

      focusVehLabel.Enabled = false;
      focusVehTextBox.Enabled = false;
      xOffsetLabel.Enabled = false;
      xOffsetTextBox.Enabled = false;
      yOffsetLabel.Enabled = false;
      yOffsetTextBox.Enabled = false;

      if (enable)
      {
        rotateAroundCheckBox.Enabled = setAsOriginCheckBox.Checked;
        globalGroupBox.Enabled = !logLightMode;
        groupBoxFocus.Enabled = !logLightMode;
      }
    }

    void LoadConfig()
    {
      float result = 0.0f;
      scale = 2.0f;
      if (float.TryParse(config.Read("scale"), out result))
        scale = result;

      if (scale <= 0.0f)
        scale = 0.1f;

      scaleTextBox.Text = scale.ToString();

      result = 0.0f;
      xOffset = 0.0f;
      if (float.TryParse(config.Read("xOffset"), out result))
        xOffset = result;

      xOffsetTextBox.Text = xOffset.ToString();

      result = 0.0f;
      yOffset = 0.0f;
      if (float.TryParse(config.Read("yOffset"), out result))
        yOffset = result;

      yOffsetTextBox.Text = yOffset.ToString();

      int intResult = 0;
      focusVehicle = 0;
      if (int.TryParse(config.Read("focusVehicle"), out intResult) && intResult >= 0)
        focusVehicle = intResult;

      focusVehTextBox.Text = focusVehicle.ToString();

      intResult = 0;
      centerOnVehicle = true;
      if (int.TryParse(config.Read("centerOnVehicle"), out intResult) && intResult == 0)
        centerOnVehicle = false;

      setAsOriginCheckBox.Checked = centerOnVehicle;

      intResult = 0;
      rotateAroundVehicle = true;
      if (int.TryParse(config.Read("rotateAroundVehicle"), out intResult) && intResult == 0)
        rotateAroundVehicle = false;

      rotateAroundCheckBox.Checked = rotateAroundVehicle;

      intResult = 0;
      logLightMode = false;
      if (int.TryParse(config.Read("logLightMode"), out intResult) && intResult == 1)
        logLightMode = true;

      lightModeCheckBox.Checked = logLightMode;

      intResult = 0;
      logPhaseAndState = true;
      if (int.TryParse(config.Read("logPhaseAndState"), out intResult) && intResult == 0)
        logPhaseAndState = false;

      logPhaseAndStateCheckBox.Checked = logPhaseAndState;

      intResult = 0;
      logDamage = true;
      if (int.TryParse(config.Read("logDamage"), out intResult) && intResult == 0)
        logDamage = false;

      logDamageCheckBox.Checked = logDamage;

      intResult = 0;
      logTiming = true;
      if (int.TryParse(config.Read("logTiming"), out intResult) && intResult == 0)
        logTiming = false;

      logTimingCheckBox.Checked = logTiming;

      intResult = 0;
      logRules = true;
      if (int.TryParse(config.Read("logRules"), out intResult) && intResult == 0)
        logRules = false;

      logRulesCheckBox.Checked = logRules;

      intResult = 0;
      enablePitInputs = true;
      if (int.TryParse(config.Read("enablePitInputs"), out intResult) && intResult == 0)
        enablePitInputs = false;

      enablePitInputsCheckBox.Checked = enablePitInputs;

      useStockCarRulesPlugin = false;
    }
  }
}
