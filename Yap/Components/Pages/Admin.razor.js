const charts = {};

const defaultOptions = {
    responsive: true,
    maintainAspectRatio: false,
    animation: { duration: 300 },
    interaction: { mode: 'index', intersect: false },
    plugins: {
        legend: {
            labels: { color: '#b5bac1', boxWidth: 12, padding: 12 }
        },
        tooltip: {
            backgroundColor: '#18191c',
            titleColor: '#f2f3f5',
            bodyColor: '#b5bac1',
            borderColor: '#2b2d31',
            borderWidth: 1,
            padding: 8
        }
    },
    scales: {
        x: {
            grid: { color: '#202225' },
            ticks: { color: '#72767d', maxTicksLimit: 8, maxRotation: 0 }
        },
        y: {
            beginAtZero: true,
            grid: { color: '#202225' },
            ticks: { color: '#72767d', precision: 0 }
        }
    },
    elements: {
        point: { radius: 0, hoverRadius: 4 },
        line: { tension: 0.3, borderWidth: 2 }
    }
};

export function createOrUpdateChart(canvasId, labels, datasets) {
    if (charts[canvasId]) {
        const chart = charts[canvasId];
        chart.data.labels = labels;
        chart.data.datasets.forEach((ds, i) => {
            if (datasets[i]) {
                ds.data = datasets[i].data;
            }
        });
        chart.update('none');
        return;
    }

    const canvas = document.getElementById(canvasId);
    if (!canvas) return;

    charts[canvasId] = new Chart(canvas.getContext('2d'), {
        type: 'line',
        data: { labels, datasets },
        options: defaultOptions
    });
}

export function destroyAllCharts() {
    for (const id in charts) {
        charts[id].destroy();
        delete charts[id];
    }
}
