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
    <div className="card p-4 flex items-center gap-3">
      <div className={`p-2.5 rounded-lg ${color} flex-shrink-0`}>
        <Icon className="h-5 w-5 text-white" />
      </div>
      <div className="min-w-0">
        <p className="text-xs sm:text-sm text-muted-foreground leading-tight">{label}</p>
        <p className="text-xl sm:text-2xl font-bold text-foreground">{value}</p>
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
      <h1 className="text-xl sm:text-2xl font-bold text-foreground">Dashboard</h1>

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
              className="flex items-center gap-3 p-4 hover:bg-muted/50 transition-colors"
            >
              <div className="flex-1 min-w-0">
                <p className="text-sm font-medium text-foreground truncate">{doc.originalFilename}</p>
                <p className="text-xs text-muted-foreground">
                  {doc.uploadedByUsername} · {new Date(doc.uploadedAt).toLocaleDateString()}
                </p>
              </div>
              <div className="flex-shrink-0"><StatusBadge status={doc.status} /></div>
            </Link>
          ))}
        </div>
      </div>
    </div>
  );
}
