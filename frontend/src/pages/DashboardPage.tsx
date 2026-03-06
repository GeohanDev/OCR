import { useQuery } from '@tanstack/react-query';
import { dashboardApi } from '../api/client';
import StatusBadge from '../components/ui/StatusBadge';
import { FileText, Clock, XCircle, CheckSquare } from 'lucide-react';
import type { DashboardKpis } from '../types';
import { Link } from 'react-router-dom';

function KpiCard({ label, value, icon: Icon, color }: {
  label: string; value: number; icon: React.ElementType; color: string;
}) {
  return (
    <div className="card p-5 flex items-center gap-4">
      <div className={`p-3 rounded-lg ${color}`}>
        <Icon className="h-6 w-6 text-white" />
      </div>
      <div>
        <p className="text-sm text-muted-foreground">{label}</p>
        <p className="text-2xl font-bold text-foreground">{value}</p>
      </div>
    </div>
  );
}

export default function DashboardPage() {
  const { data, isLoading, isError } = useQuery<DashboardKpis>({
    queryKey: ['dashboard-kpis'],
    queryFn: () => dashboardApi.getKpis().then(r => r.data),
    refetchInterval: 30000,
  });

  if (isLoading) return <div className="text-center py-12 text-muted-foreground">Loading dashboard...</div>;
  if (isError || !data) return <div className="text-center py-12 text-muted-foreground">Could not load dashboard data.</div>;

  const kpis = data;

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold text-foreground">Dashboard</h1>

      <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
        <KpiCard label="Total Documents" value={kpis.totalDocuments} icon={FileText}    color="bg-gray-500" />
        <KpiCard label="Pending Review"  value={kpis.pendingReview}  icon={Clock}       color="bg-orange-500" />
        <KpiCard label="Failed"          value={kpis.failed}         icon={XCircle}     color="bg-red-500" />
        <KpiCard label="Checked"         value={kpis.checked}        icon={CheckSquare} color="bg-blue-500" />
      </div>

      <div className="card">
        <div className="p-4 border-b border-border">
          <h2 className="font-semibold text-foreground">Recent Documents</h2>
        </div>
        <div className="divide-y divide-border">
          {kpis.recentDocuments.length === 0 && (
            <p className="p-4 text-sm text-muted-foreground">No documents yet.</p>
          )}
          {kpis.recentDocuments.map((doc) => (
            <Link
              key={doc.id}
              to={`/documents/${doc.id}`}
              className="flex items-center justify-between p-4 hover:bg-muted/50 transition-colors"
            >
              <div>
                <p className="text-sm font-medium text-foreground truncate max-w-xs">{doc.originalFilename}</p>
                <p className="text-xs text-muted-foreground">
                  {doc.uploadedByUsername} · {new Date(doc.uploadedAt).toLocaleDateString()}
                </p>
              </div>
              <StatusBadge status={doc.status} />
            </Link>
          ))}
        </div>
      </div>
    </div>
  );
}
