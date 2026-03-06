const statusConfig: Record<string, { label: string; classes: string }> = {
  Uploaded:       { label: 'Uploaded',       classes: 'bg-gray-100 text-gray-700' },
  Processing:     { label: 'Processing',     classes: 'bg-yellow-100 text-yellow-700 animate-pulse' },
  PendingReview:  { label: 'Pending Review', classes: 'bg-orange-100 text-orange-700' },
  ReviewInProgress: { label: 'In Review',   classes: 'bg-blue-100 text-blue-700' },
  Approved:       { label: 'Approved',       classes: 'bg-green-100 text-green-700' },
  Rejected:       { label: 'Rejected',       classes: 'bg-red-100 text-red-700' },
  Pushed:         { label: 'Pushed to ERP',  classes: 'bg-purple-100 text-purple-700' },
  Checked:        { label: 'Checked',        classes: 'bg-teal-100 text-teal-700' },
};

export default function StatusBadge({ status }: { status: string }) {
  const config = statusConfig[status] ?? { label: status, classes: 'bg-gray-100 text-gray-700' };
  return <span className={`badge ${config.classes}`}>{config.label}</span>;
}
