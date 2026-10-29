using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class User
{
    public int Id { get; set; }

    public string EmployeeCode { get; set; } = null!;

    public string Email { get; set; } = null!;

    public string? LoginId { get; set; }

    public string PasswordHash { get; set; } = null!;

    public string FullName { get; set; } = null!;

    public int RoleId { get; set; }

    public int? ManagerId { get; set; }

    public string? Designation { get; set; }

    public string? Department { get; set; }

    public DateOnly? DateOfBirth { get; set; }

    public DateOnly? DateOfJoining { get; set; }

    public string? Phone { get; set; }

    public string? Cnic { get; set; }

    public string? BankName { get; set; }

    public string? BankAccountTitle { get; set; }

    public string? BankAccountNumber { get; set; }

    public string? EmergencyContact { get; set; }

    public string? EmergencyPhone { get; set; }

    public string? Address { get; set; }

    public string? ProfileImage { get; set; }

    public bool? IsActive { get; set; }

    public bool? HasHrAccess { get; set; }

    public bool? HasInventoryAccess { get; set; }

    public bool? HasInvoicingAccess { get; set; }

    public bool? HasFinanceAccess { get; set; }

    public bool? HasAccountingAccess { get; set; }

    public bool? HasFbrAccess { get; set; }

    public bool? HasFixedAssetsAccess { get; set; }

    public bool? HasExecutiveAccess { get; set; }

    public bool? IsSuperAdmin { get; set; }

    public int? MaxCompaniesOwned { get; set; }

    public int? MaxCompaniesJoined { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual ICollection<AccountingVoucher> AccountingVoucherApprovedByNavigations { get; set; } = new List<AccountingVoucher>();

    public virtual ICollection<AccountingVoucher> AccountingVoucherCreatedByNavigations { get; set; } = new List<AccountingVoucher>();

    public virtual ICollection<Announcement> Announcements { get; set; } = new List<Announcement>();

    public virtual ICollection<AssetTransfer> AssetTransferApprovedByNavigations { get; set; } = new List<AssetTransfer>();

    public virtual ICollection<AssetTransfer> AssetTransferCreatedByNavigations { get; set; } = new List<AssetTransfer>();

    public virtual ICollection<AttendanceCorrection> AttendanceCorrections { get; set; } = new List<AttendanceCorrection>();

    public virtual ICollection<Attendance> Attendances { get; set; } = new List<Attendance>();

    public virtual ICollection<BankReconciliation> BankReconciliations { get; set; } = new List<BankReconciliation>();

    public virtual ICollection<ChangeRequest> ChangeRequestReviewedByNavigations { get; set; } = new List<ChangeRequest>();

    public virtual ICollection<ChangeRequest> ChangeRequestUsers { get; set; } = new List<ChangeRequest>();

    public virtual ICollection<Company> Companies { get; set; } = new List<Company>();

    public virtual ICollection<CompanyInvitation> CompanyInvitations { get; set; } = new List<CompanyInvitation>();

    public virtual ICollection<CompanyMembership> CompanyMemberships { get; set; } = new List<CompanyMembership>();

    public virtual ICollection<ConsumptionVoucher> ConsumptionVoucherApprovedByNavigations { get; set; } = new List<ConsumptionVoucher>();

    public virtual ICollection<ConsumptionVoucher> ConsumptionVoucherCreatedByNavigations { get; set; } = new List<ConsumptionVoucher>();

    public virtual ICollection<DigitalFile> DigitalFileUsers { get; set; } = new List<DigitalFile>();

    public virtual ICollection<DigitalFile> DigitalFileVerifiedByNavigations { get; set; } = new List<DigitalFile>();

    public virtual ICollection<ExecAccountSelection> ExecAccountSelections { get; set; } = new List<ExecAccountSelection>();

    public virtual ICollection<FbrInvoiceRecord> FbrInvoiceRecords { get; set; } = new List<FbrInvoiceRecord>();

    public virtual ICollection<GlobalLimit> GlobalLimits { get; set; } = new List<GlobalLimit>();

    public virtual ICollection<InvInvoice> InvInvoiceApprovedByNavigations { get; set; } = new List<InvInvoice>();

    public virtual ICollection<InvInvoice> InvInvoiceCreatedByNavigations { get; set; } = new List<InvInvoice>();

    public virtual ICollection<InvPurchaseInvoice> InvPurchaseInvoiceApprovedByNavigations { get; set; } = new List<InvPurchaseInvoice>();

    public virtual ICollection<InvPurchaseInvoice> InvPurchaseInvoiceCreatedByNavigations { get; set; } = new List<InvPurchaseInvoice>();

    public virtual ICollection<InvPurchaseOrder> InvPurchaseOrderApprovedByNavigations { get; set; } = new List<InvPurchaseOrder>();

    public virtual ICollection<InvPurchaseOrder> InvPurchaseOrderCreatedByNavigations { get; set; } = new List<InvPurchaseOrder>();

    public virtual ICollection<InvPurchaseReturn> InvPurchaseReturnApprovedByNavigations { get; set; } = new List<InvPurchaseReturn>();

    public virtual ICollection<InvPurchaseReturn> InvPurchaseReturnCreatedByNavigations { get; set; } = new List<InvPurchaseReturn>();

    public virtual ICollection<InvSalesOrder> InvSalesOrderApprovedByNavigations { get; set; } = new List<InvSalesOrder>();

    public virtual ICollection<InvSalesOrder> InvSalesOrderCreatedByNavigations { get; set; } = new List<InvSalesOrder>();

    public virtual ICollection<InvSalesReturn> InvSalesReturnApprovedByNavigations { get; set; } = new List<InvSalesReturn>();

    public virtual ICollection<InvSalesReturn> InvSalesReturnCreatedByNavigations { get; set; } = new List<InvSalesReturn>();

    public virtual ICollection<InvStockMovement> InvStockMovements { get; set; } = new List<InvStockMovement>();

    public virtual ICollection<User> InverseManager { get; set; } = new List<User>();

    public virtual ICollection<JournalEntry> JournalEntries { get; set; } = new List<JournalEntry>();

    public virtual ICollection<KanbanBoard> KanbanBoards { get; set; } = new List<KanbanBoard>();

    public virtual ICollection<KanbanTask> KanbanTasks { get; set; } = new List<KanbanTask>();

    public virtual ICollection<LeaveApproval> LeaveApprovals { get; set; } = new List<LeaveApproval>();

    public virtual ICollection<LeaveQuota> LeaveQuota { get; set; } = new List<LeaveQuota>();

    public virtual ICollection<LeaveRequest> LeaveRequests { get; set; } = new List<LeaveRequest>();

    public virtual ICollection<LoanAdvanceRequest> LoanAdvanceRequestApprovedByNavigations { get; set; } = new List<LoanAdvanceRequest>();

    public virtual ICollection<LoanAdvanceRequest> LoanAdvanceRequestUsers { get; set; } = new List<LoanAdvanceRequest>();

    public virtual User? Manager { get; set; }

    public virtual ICollection<NotificationRecipient> NotificationRecipients { get; set; } = new List<NotificationRecipient>();

    public virtual ICollection<Notification> Notifications { get; set; } = new List<Notification>();

    public virtual ICollection<OvertimeAccount> OvertimeAccounts { get; set; } = new List<OvertimeAccount>();

    public virtual ICollection<PaymentAllocation> PaymentAllocations { get; set; } = new List<PaymentAllocation>();

    public virtual ICollection<PayrollAuditLog> PayrollAuditLogs { get; set; } = new List<PayrollAuditLog>();

    public virtual ICollection<PayrollProfile> PayrollProfiles { get; set; } = new List<PayrollProfile>();

    public virtual ICollection<PayrollRun> PayrollRunApprovedByNavigations { get; set; } = new List<PayrollRun>();

    public virtual ICollection<PayrollRun> PayrollRunProcessedByNavigations { get; set; } = new List<PayrollRun>();

    public virtual ICollection<PayrollSlip> PayrollSlips { get; set; } = new List<PayrollSlip>();

    public virtual ICollection<PerformanceGoal> PerformanceGoalCreatedByNavigations { get; set; } = new List<PerformanceGoal>();

    public virtual ICollection<PerformanceGoal> PerformanceGoalUsers { get; set; } = new List<PerformanceGoal>();

    public virtual ICollection<PerformanceReview> PerformanceReviewReviewers { get; set; } = new List<PerformanceReview>();

    public virtual ICollection<PerformanceReview> PerformanceReviewUsers { get; set; } = new List<PerformanceReview>();

    public virtual ICollection<PfContribution> PfContributions { get; set; } = new List<PfContribution>();

    public virtual ICollection<PfLedger> PfLedgers { get; set; } = new List<PfLedger>();

    public virtual ICollection<PfLoanRequest> PfLoanRequestApprovedByNavigations { get; set; } = new List<PfLoanRequest>();

    public virtual ICollection<PfLoanRequest> PfLoanRequestUsers { get; set; } = new List<PfLoanRequest>();

    public virtual ICollection<PfProfitDistribution> PfProfitDistributions { get; set; } = new List<PfProfitDistribution>();

    public virtual ICollection<PfSettlement> PfSettlementSettledByNavigations { get; set; } = new List<PfSettlement>();

    public virtual ICollection<PfSettlement> PfSettlementUsers { get; set; } = new List<PfSettlement>();

    public virtual ICollection<PfWithdrawalRequest> PfWithdrawalRequestApprovedByNavigations { get; set; } = new List<PfWithdrawalRequest>();

    public virtual ICollection<PfWithdrawalRequest> PfWithdrawalRequestUsers { get; set; } = new List<PfWithdrawalRequest>();

    public virtual ICollection<ProjectTask> ProjectTasks { get; set; } = new List<ProjectTask>();

    public virtual ICollection<Project> Projects { get; set; } = new List<Project>();

    public virtual ICollection<RegistrationRequest> RegistrationRequests { get; set; } = new List<RegistrationRequest>();

    public virtual Role Role { get; set; } = null!;

    public virtual ICollection<SalaryRevision> SalaryRevisionApprovedByNavigations { get; set; } = new List<SalaryRevision>();

    public virtual ICollection<SalaryRevision> SalaryRevisionUsers { get; set; } = new List<SalaryRevision>();

    public virtual ICollection<ScrapVoucher> ScrapVoucherApprovedByNavigations { get; set; } = new List<ScrapVoucher>();

    public virtual ICollection<ScrapVoucher> ScrapVoucherCreatedByNavigations { get; set; } = new List<ScrapVoucher>();

    public virtual ICollection<StockAdjustmentVoucher> StockAdjustmentVoucherApprovedByNavigations { get; set; } = new List<StockAdjustmentVoucher>();

    public virtual ICollection<StockAdjustmentVoucher> StockAdjustmentVoucherCreatedByNavigations { get; set; } = new List<StockAdjustmentVoucher>();

    public virtual ICollection<StockLedger> StockLedgers { get; set; } = new List<StockLedger>();

    public virtual ICollection<StockTake> StockTakeApprovedByNavigations { get; set; } = new List<StockTake>();

    public virtual ICollection<StockTake> StockTakeCreatedByNavigations { get; set; } = new List<StockTake>();

    public virtual ICollection<TeamEvent> TeamEvents { get; set; } = new List<TeamEvent>();

    public virtual ICollection<TimesheetApproval> TimesheetApprovals { get; set; } = new List<TimesheetApproval>();

    public virtual ICollection<TimesheetMergedReport> TimesheetMergedReports { get; set; } = new List<TimesheetMergedReport>();

    public virtual ICollection<TimesheetWeek> TimesheetWeekApprovedByNavigations { get; set; } = new List<TimesheetWeek>();

    public virtual ICollection<TimesheetWeek> TimesheetWeekUsers { get; set; } = new List<TimesheetWeek>();

    public virtual ICollection<UserPermission> UserPermissions { get; set; } = new List<UserPermission>();

    public virtual ICollection<UserSession> UserSessions { get; set; } = new List<UserSession>();
}
