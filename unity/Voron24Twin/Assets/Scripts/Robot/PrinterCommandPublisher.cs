using System;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosPrinterCommand = RosMessageTypes.Voron24.PrinterCommandMsg;

namespace Voron24.Robot
{
    /// <summary>
    /// Publishes commands to the ROS printer command topic.
    /// Command names, argument units, and payload semantics follow 00_interface_contract.md.
    /// </summary>
    public class PrinterCommandPublisher : MonoBehaviour
    {
        [SerializeField]
        private string topic = "/printer/cmd";

        [Tooltip("Jog distance in millimetres.")]
        [SerializeField]
        private float jogStepMm = 1f;

        [Tooltip("Speed multiplier sent with set_speed.")]
        [SerializeField]
        private float speedScale = 1f;

        private ROSConnection ros;

        private void Awake()
        {
            ros = ROSConnection.GetOrCreateInstance();
            ros.RegisterPublisher<RosPrinterCommand>(topic);
        }

        public void JogXPlus() => Jog(jogStepMm, 0f, 0f);
        public void JogXMinus() => Jog(-jogStepMm, 0f, 0f);
        public void JogYPlus() => Jog(0f, jogStepMm, 0f);
        public void JogYMinus() => Jog(0f, -jogStepMm, 0f);
        public void JogZPlus() => Jog(0f, 0f, jogStepMm);
        public void JogZMinus() => Jog(0f, 0f, -jogStepMm);

        public void Jog(float dxMm, float dyMm, float dzMm)
        {
            Publish("jog", new[] { dxMm, dyMm, dzMm }, string.Empty);
        }

        public void Home()
        {
            HomeAll();
        }

        public void HomeAll()
        {
            Publish("home", Array.Empty<float>(), "XYZ");
        }

        public void HomeX()
        {
            Publish("home", Array.Empty<float>(), "X");
        }

        public void HomeY()
        {
            Publish("home", Array.Empty<float>(), "Y");
        }

        public void HomeZ()
        {
            Publish("home", Array.Empty<float>(), "Z");
        }

        public void Pause()
        {
            Publish("pause", Array.Empty<float>(), string.Empty);
        }

        public void Resume()
        {
            Publish("resume", Array.Empty<float>(), string.Empty);
        }

        public void Stop()
        {
            Publish("stop", Array.Empty<float>(), string.Empty);
        }

        public void LoadGcode(string absolutePath)
        {
            Publish("load_gcode", Array.Empty<float>(), absolutePath);
        }

        public void SetSpeed()
        {
            SetSpeed(speedScale);
        }

        public void SetSpeed(float scale)
        {
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale < 0f)
            {
                Debug.LogError("[PrinterCommand] Speed scale must be finite and non-negative.");
                return;
            }

            Publish("set_speed", new[] { scale }, string.Empty);
        }

        private void Publish(string command, float[] args, string payload)
        {
            if (ros == null)
            {
                Debug.LogError("[PrinterCommand] ROS connection is not initialized.");
                return;
            }

            var message = new RosPrinterCommand
            {
                command = command,
                args = args ?? Array.Empty<float>(),
                payload = payload ?? string.Empty
            };

            ros.Publish(topic, message);
        }
    }
}
