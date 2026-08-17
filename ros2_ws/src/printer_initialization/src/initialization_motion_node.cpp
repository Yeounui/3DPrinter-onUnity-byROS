#include <algorithm>
#include <chrono>
#include <cstddef>
#include <functional>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

#include <rclcpp/rclcpp.hpp>
#include <sensor_msgs/msg/joint_state.hpp>

#include "printer_initialization_msgs/srv/evaluate_collision.hpp"
#include "printer_initialization_msgs/srv/evaluate_collision_policy.hpp"

using namespace std::chrono_literals;

class InitializationMotionNode : public rclcpp::Node
{
  using Collision = printer_initialization_msgs::srv::EvaluateCollision;
  using Policy = printer_initialization_msgs::srv::EvaluateCollisionPolicy;

public:
  InitializationMotionNode()
  : Node("initialization_motion_node")
  {
    active_joint_ = declare_parameter<std::string>("active_joint", "base_to_z_gantry");
    direction_ = declare_parameter<int>("direction", -1);
    step_size_ = declare_parameter<double>("step_size", 1.0);
    temporary_search_range_ = declare_parameter<int>("temporary_search_range", 1000);
    joint_names_ = {"base_to_z_gantry", "z_gantry_to_x_beam_black", "x_beam_black_to_toolhead"};
    if (direction_ != -1 && direction_ != 1) {
      throw std::runtime_error("direction must be -1 or 1");
    }
    if (step_size_ <= 0.0 || temporary_search_range_ < 1) {
      throw std::runtime_error("step_size must be positive and search range must be at least 1");
    }
    const auto found = std::find(joint_names_.begin(), joint_names_.end(), active_joint_);
    if (found == joint_names_.end()) {
      throw std::runtime_error("active_joint is not a known printer joint");
    }
    active_joint_index_ = static_cast<std::size_t>(std::distance(joint_names_.begin(), found));
    positions_.assign(joint_names_.size(), 0.0);
    start_position_ = positions_[active_joint_index_];

    joint_state_publisher_ = create_publisher<sensor_msgs::msg::JointState>("/joint_states", 10);
    collision_client_ = create_client<Collision>("/initialization/evaluate_collision");
    policy_client_ = create_client<Policy>("/initialization/evaluate_collision_policy");
    publish_position();
    timer_ = create_wall_timer(100ms, std::bind(&InitializationMotionNode::request_next_step, this));
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

  void request_next_step()
  {
    if (finished_ || request_in_flight_) return;
    if (completed_steps_ >= temporary_search_range_) {
      finished_ = true;
      RCLCPP_INFO(get_logger(),
        "STOP reason=SEARCH_RANGE active_joint=%s safe_position=%.3f total_distance=%.3f "
        "completed_steps=%d limit=%d",
        active_joint_.c_str(), positions_[active_joint_index_], total_distance(),
        completed_steps_, temporary_search_range_);
      return;
    }
    if (!collision_client_->service_is_ready() || !policy_client_->service_is_ready()) {
      RCLCPP_INFO_THROTTLE(get_logger(), *get_clock(), 2000, "Waiting for collision services.");
      return;
    }

    candidate_positions_ = positions_;
    candidate_positions_[active_joint_index_] += direction_ * step_size_;
    auto request = std::make_shared<Collision::Request>();
    request->joint_names = joint_names_;
    request->joint_positions = candidate_positions_;
    request_in_flight_ = true;
    collision_client_->async_send_request(request,
      std::bind(&InitializationMotionNode::on_collision_result, this, std::placeholders::_1));
  }

  void on_collision_result(rclcpp::Client<Collision>::SharedFuture future)
  {
    const auto response = future.get();
    collision_links_a_ = response->link_a;
    collision_links_b_ = response->link_b;
    policy_index_ = 0;
    if (collision_links_a_.empty()) {
      RCLCPP_INFO(get_logger(),
        "STEP reason=NO_COLLISION active_joint=%s current=%.3f candidate=%.3f",
        active_joint_.c_str(), positions_[active_joint_index_], candidate_positions_[active_joint_index_]);
      accept_candidate();
    } else {
      request_next_policy();
    }
  }

  void request_next_policy()
  {
    if (policy_index_ >= collision_links_a_.size()) {
      accept_candidate();
      return;
    }
    auto request = std::make_shared<Policy::Request>();
    request->active_joint = active_joint_;
    request->direction = direction_;
    request->link_a = collision_links_a_[policy_index_];
    request->link_b = collision_links_b_[policy_index_];
    policy_client_->async_send_request(request,
      std::bind(&InitializationMotionNode::on_policy_result, this, std::placeholders::_1));
  }

  void on_policy_result(rclcpp::Client<Policy>::SharedFuture future)
  {
    const auto response = future.get();
    if (!response->ignored) {
      if (active_joint_ == "base_to_z_gantry" && direction_ < 0 &&
        is_nozzle_bed_contact(policy_index_) && bed_refinements_ < 3)
      {
        step_size_ /= 2.0;
        ++bed_refinements_;
        request_in_flight_ = false;
        RCLCPP_INFO(get_logger(),
          "BED_REFINE contact=%s<->%s refinement=%d/3 next_step=%.3f "
          "safe_position=%.3f total_distance=%.3f",
          collision_links_a_[policy_index_].c_str(), collision_links_b_[policy_index_].c_str(),
          bed_refinements_, step_size_, positions_[active_joint_index_], total_distance());
        return;
      }
      finished_ = true;
      request_in_flight_ = false;
      RCLCPP_INFO(get_logger(),
        "STOP reason=COLLISION active_joint=%s safe_position=%.3f rejected_position=%.3f "
        "total_distance=%.3f steps=%d link_a=%s link_b=%s rule_id=%s",
        active_joint_.c_str(), positions_[active_joint_index_],
        candidate_positions_[active_joint_index_], total_distance(), completed_steps_,
        collision_links_a_[policy_index_].c_str(), collision_links_b_[policy_index_].c_str(),
        response->rule_id.c_str());
      return;
    }
    RCLCPP_INFO(get_logger(),
      "COLLISION_IGNORED active_joint=%s candidate=%.3f link_a=%s link_b=%s rule_id=%s",
      active_joint_.c_str(), candidate_positions_[active_joint_index_],
      collision_links_a_[policy_index_].c_str(), collision_links_b_[policy_index_].c_str(),
      response->rule_id.c_str());
    ++policy_index_;
    request_next_policy();
  }

  void accept_candidate()
  {
    positions_ = candidate_positions_;
    ++completed_steps_;
    request_in_flight_ = false;
    publish_position();
  }

  double total_distance() const
  {
    return std::abs(positions_[active_joint_index_] - start_position_);
  }

  bool is_nozzle_bed_contact(std::size_t index) const
  {
    const auto & first = collision_links_a_[index];
    const auto & second = collision_links_b_[index];
    return (first == "base_link" && second == "toolhead_link") ||
      (first == "toolhead_link" && second == "base_link");
  }

  std::string active_joint_;
  int direction_{};
  double step_size_{};
  int temporary_search_range_{};
  std::vector<std::string> joint_names_;
  std::size_t active_joint_index_{};
  std::vector<double> positions_;
  std::vector<double> candidate_positions_;
  std::vector<std::string> collision_links_a_;
  std::vector<std::string> collision_links_b_;
  std::size_t policy_index_{};
  int completed_steps_{};
  double start_position_{};
  int bed_refinements_{};
  bool request_in_flight_{};
  bool finished_{};
  rclcpp::TimerBase::SharedPtr timer_;
  rclcpp::Publisher<sensor_msgs::msg::JointState>::SharedPtr joint_state_publisher_;
  rclcpp::Client<Collision>::SharedPtr collision_client_;
  rclcpp::Client<Policy>::SharedPtr policy_client_;
};

int main(int argc, char ** argv)
{
  rclcpp::init(argc, argv);
  rclcpp::spin(std::make_shared<InitializationMotionNode>());
  rclcpp::shutdown();
  return 0;
}
