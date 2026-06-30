namespace ExecPlan.Domain.Enums;

public enum UserRole { SystemAdmin = 0, PlanManager = 1, TeamLeader = 2, TeamMember = 3 }
public enum PlanType { Daily, Weekly, Emergency, Guard, Transport, Maintenance, It, Inspection, General }
public enum PlanStatus { Draft = 0, Ready = 1 }
public enum ShiftBand { Morning = 0, Evening = 1, Night = 2 }
public enum ActivationStatus { Active = 0, Closed = 1 }
public enum ParticipantStatus { Pending = 0, Ready = 1, Escalated = 2, Inducted = 3 }
public enum ExecTaskStatus { Pending = 0, Done = 1 }
public enum NotificationKind { Notification = 0, Broadcast = 1 }
public enum ContactKind { Contact = 0, Emergency = 1 }
