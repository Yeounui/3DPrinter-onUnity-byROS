#include <algorithm>
#include <chrono>
#include <cmath>
#include <fstream>
#include <future>
#include <memory>
#include <string>
#include <thread>
#include <vector>

#include <rclcpp/rclcpp.hpp>
#include <sensor_msgs/msg/joint_state.hpp>
#include <yaml-cpp/yaml.h>

#include "printer_initialization_msgs/msg/initialization_result.hpp"
#include "printer_initialization_msgs/srv/evaluate_collision.hpp"
#include "printer_initialization_msgs/srv/evaluate_collision_policy.hpp"
#include "printer_initialization_msgs/srv/set_gantry_base_exception.hpp"

using namespace std::chrono_literals;

class InitializationManagerNode : public rclcpp::Node
{
  using Collision = printer_initialization_msgs::srv::EvaluateCollision;
  using Policy = printer_initialization_msgs::srv::EvaluateCollisionPolicy;
  using SetException = printer_initialization_msgs::srv::SetGantryBaseException;
  using Result = printer_initialization_msgs::msg::InitializationResult;

public:
  InitializationManagerNode()
  : Node("initialization_manager_node")
  {
    step_size_ = declare_parameter<double>("step_size", 1.0);
    temporary_search_range_ = declare_parameter<int>("temporary_search_range", 1000);
    refinement_count_ = declare_parameter<int>("refinement_count", 3);
    final_raise_ = declare_parameter<double>("final_raise", 0.2);
    calibration_file_ = declare_parameter<std::string>(
      "calibration_file", "src/printer_initialization/config/calibration.yaml");
    joint_names_ = {"base_to_z_gantry", "z_gantry_to_x_beam_black", "x_beam_black_to_toolhead"};
    positions_.assign(3, 0.0);
    joint_state_publisher_ = create_publisher<sensor_msgs::msg::JointState>("/joint_states", 10);
    result_publisher_ = create_publisher<Result>(
      "/initialization/result", rclcpp::QoS(1).transient_local());
    collision_client_ = create_client<Collision>("/initialization/evaluate_collision");
    policy_client_ = create_client<Policy>("/initialization/evaluate_collision_policy");
    exception_client_ = create_client<SetException>("/initialization/set_gantry_base_exception");
    worker_ = std::thread(&InitializationManagerNode::run, this);
  }

  ~InitializationManagerNode() override
  {
    if (worker_.joinable()) worker_.join();
  }

private:
  void publish_position()
  {
    sensor_msgs::msg::JointState message;
    message.header.stamp = get_clock()->now();
    message.name = joint_names_;
    message.position = positions_;
    joint_state_publisher_->publish(message);
  }

  bool evaluate_candidate(const std::string & joint, int direction,
    const std::vector<double> & candidate, std::string & stop_a, std::string & stop_b)
  {
    if (!collision_client_->wait_for_service(5s)) {
      stop_a = "service"; stop_b = "evaluate_collision"; return false;
    }
    auto request = std::make_shared<Collision::Request>();
    request->joint_names = joint_names_;
    request->joint_positions = candidate;
    auto future = collision_client_->async_send_request(request);
    if (future.wait_for(10s) != std::future_status::ready) {
      stop_a = "service"; stop_b = "evaluate_collision"; return false;
    }
    const auto response = future.get();
    for (std::size_t index = 0; index < response->link_a.size(); ++index) {
      if (!policy_client_->wait_for_service(5s)) {
        stop_a = "service"; stop_b = "evaluate_collision_policy"; return false;
      }
      auto policy = std::make_shared<Policy::Request>();
      policy->active_joint = joint;
      policy->direction = direction;
      policy->link_a = response->link_a[index];
      policy->link_b = response->link_b[index];
      policy->z_position = candidate[0];
      policy->calibration_phase = calibration_phase_;
      auto policy_future = policy_client_->async_send_request(policy);
      if (policy_future.wait_for(10s) != std::future_status::ready) {
        stop_a = "service"; stop_b = "evaluate_collision_policy"; return false;
      }
      const auto decision = policy_future.get();
      if (!decision->ignored) {
        stop_a = response->link_a[index];
        stop_b = response->link_b[index];
        return false;
      }
    }
    return true;
  }

  bool is_bed_pair(const std::string & first, const std::string & second) const
  {
    return (first == "base_link" && second == "toolhead_link") ||
      (first == "toolhead_link" && second == "base_link");
  }

  bool search_direction(std::size_t axis, int direction, const std::string & label,
    double & boundary, double & bed_contact, bool bed_mode,
    std::string & stop_a, std::string & stop_b)
  {
    double step = step_size_;
    int refinements = 0;
    int attempts = 0;
    while (attempts < temporary_search_range_) {
      auto candidate = positions_;
      candidate[axis] += direction * step;
      ++attempts;
      if (evaluate_candidate(joint_names_[axis], direction, candidate, stop_a, stop_b)) {
        positions_ = candidate;
        publish_position();
        RCLCPP_INFO(get_logger(),
          "STEP axis=%s direction=%+d attempt=%d position=%.3f distance_from_initial=%.3f",
          label.c_str(), direction, attempts, positions_[axis], std::abs(positions_[axis]));
        continue;
      }
      if (stop_a == "service") return false;
      if (bed_mode && is_bed_pair(stop_a, stop_b)) {
        if (refinements < refinement_count_) {
          step /= 2.0;
          ++refinements;
          RCLCPP_INFO(get_logger(),
            "BED_REFINE axis=%s attempt=%d refinement=%d/%d next_step=%.3f "
            "safe_position=%.3f distance_from_initial=%.3f link_a=%s link_b=%s",
            label.c_str(), attempts, refinements, refinement_count_, step,
            positions_[axis], std::abs(positions_[axis]), stop_a.c_str(), stop_b.c_str());
          continue;
        }
        boundary = positions_[axis];
        bed_contact = positions_[axis];
        RCLCPP_INFO(get_logger(),
          "STOP reason=BED_CONTACT axis=%s attempts=%d safe_position=%.3f "
          "distance_from_initial=%.3f link_a=%s link_b=%s",
          label.c_str(), attempts, positions_[axis], std::abs(positions_[axis]),
          stop_a.c_str(), stop_b.c_str());
        return true;
      }
      boundary = positions_[axis];
      RCLCPP_INFO(get_logger(),
        "STOP reason=COLLISION axis=%s direction=%+d attempts=%d safe_position=%.3f "
        "distance_from_initial=%.3f rejected_position=%.3f link_a=%s link_b=%s",
        label.c_str(), direction, attempts, positions_[axis], std::abs(positions_[axis]),
        candidate[axis], stop_a.c_str(), stop_b.c_str());
      return true;
    }
    boundary = positions_[axis];
    RCLCPP_INFO(get_logger(),
      "STOP reason=SEARCH_RANGE axis=%s direction=%+d attempts=%d safe_position=%.3f "
      "distance_from_initial=%.3f limit=%d",
      label.c_str(), direction, attempts, positions_[axis], std::abs(positions_[axis]),
      temporary_search_range_);
    return !bed_mode;
  }

  bool move_to(std::size_t axis, double target, std::string & stop_a, std::string & stop_b)
  {
    const int direction = target >= positions_[axis] ? 1 : -1;
    int attempts = 0;
    while (std::abs(target - positions_[axis]) > 1e-9) {
      auto candidate = positions_;
      candidate[axis] += direction * std::min(step_size_, std::abs(target - positions_[axis]));
      ++attempts;
      if (!evaluate_candidate(joint_names_[axis], direction, candidate, stop_a, stop_b)) {
        RCLCPP_ERROR(get_logger(), "REFERENCE_MOVE_FAILED axis=%s attempt=%d link_a=%s link_b=%s",
          joint_names_[axis].c_str(), attempts, stop_a.c_str(), stop_b.c_str());
        return false;
      }
      positions_ = candidate;
      publish_position();
      RCLCPP_INFO(get_logger(),
        "REFERENCE_STEP axis=%s direction=%+d attempt=%d position=%.3f "
        "distance_from_initial=%.3f target=%.3f",
        joint_names_[axis].c_str(), direction, attempts, positions_[axis],
        std::abs(positions_[axis]), target);
    }
    return true;
  }

  bool calibrate_gantry_base_exception(double contact, double & clear_position)
  {
    for (int attempt = 1; attempt <= temporary_search_range_; ++attempt) {
      auto candidate = positions_;
      candidate[0] = contact + attempt * step_size_;
      if (!collision_client_->wait_for_service(5s)) return false;
      auto request = std::make_shared<Collision::Request>();
      request->joint_names = joint_names_;
      request->joint_positions = candidate;
      auto future = collision_client_->async_send_request(request);
      if (future.wait_for(10s) != std::future_status::ready) return false;
      const auto response = future.get();
      bool gantry_base_collision = false;
      for (std::size_t i = 0; i < response->link_a.size(); ++i) {
        if ((response->link_a[i] == "base_link" && response->link_b[i] == "z_gantry_link") ||
          (response->link_a[i] == "z_gantry_link" && response->link_b[i] == "base_link"))
        {
          gantry_base_collision = true;
          break;
        }
      }
      positions_ = candidate;
      publish_position();
      RCLCPP_INFO(get_logger(),
        "GANTRY_BASE_CLEARANCE attempt=%d z=%.3f distance_from_initial=%.3f collision=%s",
        attempt, positions_[0], std::abs(positions_[0]), gantry_base_collision ? "true" : "false");
      if (!gantry_base_collision) {
        clear_position = positions_[0];
        if (!exception_client_->wait_for_service(5s)) return false;
        auto exception = std::make_shared<SetException::Request>();
        exception->z_min = std::min(contact, clear_position);
        exception->z_max = std::max(contact, clear_position);
        auto exception_future = exception_client_->async_send_request(exception);
        if (exception_future.wait_for(10s) != std::future_status::ready ||
          !exception_future.get()->accepted) return false;
        calibration_phase_ = false;
        RCLCPP_INFO(get_logger(),
          "GANTRY_BASE_EXCEPTION z_min=%.3f z_max=%.3f rule=measured_range",
          exception->z_min, exception->z_max);
        RCLCPP_INFO(get_logger(),
          "RETURN_TO_INITIALIZATION_POSITION target_z=%.3f from_z=%.3f",
          contact + final_raise_, positions_[0]);
        return true;
      }
    }
    return false;
  }

  void save_result(const Result & result)
  {
    std::ofstream output(calibration_file_);
    if (!output) throw std::runtime_error("Cannot write calibration file: " + calibration_file_);
    output << "axis_limits:\n"
      << "  x_min: " << result.x_min << "\n  x_max: " << result.x_max << "\n"
      << "  y_min: " << result.y_min << "\n  y_max: " << result.y_max << "\n"
      << "  z_min: " << result.z_min << "\n  z_max: " << result.z_max << "\n"
      << "reference_xy:\n  x: " << result.reference_x << "\n  y: " << result.reference_y << "\n"
      << "z_reference:\n  bed_contact: " << result.z_bed_contact
      << "\n  safe_floor: " << result.z_safe_floor << "\n"
      << "gantry_base_exception:\n  z_min: " << result.gantry_base_exception_z_min
      << "\n  z_max: " << result.gantry_base_exception_z_max << "\n";
  }

  void run()
  {
    Result result;
    result.calibration_file = calibration_file_;
    publish_position();
    std::string stop_a, stop_b;
    bool ok = true;
    ok = search_direction(2, -1, "X_NEGATIVE", result.x_min, result.z_bed_contact, false, stop_a, stop_b);
    ok = ok && search_direction(2, 1, "X_POSITIVE", result.x_max, result.z_bed_contact, false, stop_a, stop_b);
    ok = ok && search_direction(1, -1, "Y_NEGATIVE", result.y_min, result.z_bed_contact, false, stop_a, stop_b);
    ok = ok && search_direction(1, 1, "Y_POSITIVE", result.y_max, result.z_bed_contact, false, stop_a, stop_b);
    result.reference_x = (result.x_min + result.x_max) / 2.0;
    result.reference_y = (result.y_min + result.y_max) / 2.0;
    if (ok) ok = move_to(2, result.reference_x, stop_a, stop_b);
    if (ok) ok = move_to(1, result.reference_y, stop_a, stop_b);
    if (ok) ok = search_direction(0, 1, "Z_POSITIVE", result.z_max, result.z_bed_contact, false, stop_a, stop_b);
    if (ok) ok = search_direction(0, -1, "Z_NEGATIVE_BED", result.z_min, result.z_bed_contact, true, stop_a, stop_b);
    if (ok) {
      result.gantry_base_exception_z_min = result.z_bed_contact;
      ok = calibrate_gantry_base_exception(
        result.z_bed_contact, result.gantry_base_exception_z_max);
    }
    if (ok) {
      result.z_safe_floor = result.z_bed_contact + final_raise_;
      ok = move_to(0, result.z_safe_floor, stop_a, stop_b);
    }
    result.completed = ok;
    result.final_stage = ok ? "READY" : "ERROR";
    result.stop_reason = ok ? "BED_CONTACT_AND_SAFE_FLOOR" : "INITIALIZATION_ERROR";
    result.stopping_link_a = stop_a;
    result.stopping_link_b = stop_b;
    result_publisher_->publish(result);
    if (ok) {
      save_result(result);
      RCLCPP_INFO(get_logger(),
        "INITIALIZATION_COMPLETE stage=READY x=[%.3f,%.3f] y=[%.3f,%.3f] z=[%.3f,%.3f] "
        "reference=(%.3f,%.3f) bed_contact=%.3f safe_floor=%.3f",
        result.x_min, result.x_max, result.y_min, result.y_max, result.z_min, result.z_max,
        result.reference_x, result.reference_y, result.z_bed_contact, result.z_safe_floor);
    } else {
      RCLCPP_ERROR(get_logger(), "INITIALIZATION_FAILED link_a=%s link_b=%s",
        stop_a.c_str(), stop_b.c_str());
    }
  }

  double step_size_{};
  int temporary_search_range_{};
  int refinement_count_{};
  double final_raise_{};
  std::string calibration_file_;
  std::vector<std::string> joint_names_;
  std::vector<double> positions_;
  rclcpp::Publisher<sensor_msgs::msg::JointState>::SharedPtr joint_state_publisher_;
  rclcpp::Publisher<Result>::SharedPtr result_publisher_;
  rclcpp::Client<Collision>::SharedPtr collision_client_;
  rclcpp::Client<Policy>::SharedPtr policy_client_;
  rclcpp::Client<SetException>::SharedPtr exception_client_;
  bool calibration_phase_{true};
  std::thread worker_;
};

int main(int argc, char ** argv)
{
  rclcpp::init(argc, argv);
  rclcpp::spin(std::make_shared<InitializationManagerNode>());
  rclcpp::shutdown();
  return 0;
}
