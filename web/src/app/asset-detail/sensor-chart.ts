import { Component, DestroyRef, ElementRef, afterNextRender, afterRenderEffect, inject, input } from '@angular/core';
import { Chart, Decimation, Filler, LineController, LineElement, LinearScale, PointElement, Plugin } from 'chart.js';
import { Reading, Sensor, sensorValue } from '../control-room/live';
import { RuleLine } from './replay';

// Chart.js used directly (#13); registering only what the charts draw keeps the lazy chunk small.
Chart.register(Decimation, Filler, LineController, LineElement, LinearScale, PointElement);

const sensorNames: Record<Sensor, string> = {
  SpeedKph: 'Speed',
  EngineTempC: 'Engine temp',
  FuelPct: 'Fuel',
  VibrationMmS: 'Vibration',
  BatteryV: 'Battery',
};

/** Fixed axis bounds; other sensors fit their data and rule lines. */
const axis: Partial<Record<Sensor, { min: number; max: number }>> = { FuelPct: { min: 0, max: 100 } };

const day = 86_400_000;

/** A palette colour (styles.css), read when drawing because Chart.js paints a canvas, not CSS. */
const colour = (element: Element, name: string) => getComputedStyle(element).getPropertyValue(name).trim();

/** Time ticks: the clock, plus the date once the range spans more than a day. */
function tickLabel(time: number, span: number): string {
  const clock: Intl.DateTimeFormatOptions = { hour: '2-digit', minute: '2-digit' };
  return new Date(time).toLocaleString([], span > day ? { ...clock, day: 'numeric', month: 'short' } : clock);
}

/**
 * One sensor over the replay range by Device time, with each rule's threshold (dashed) and clear point (dotted) drawn
 * across it and a cursor line at the replay time.
 */
@Component({
  selector: 'app-sensor-chart',
  template: '<canvas role="img" [attr.aria-label]="sensorNames[sensor()] + \' over the replay range\'"></canvas>',
  styles: ':host { display: block; position: relative; min-width: 0; }',
})
export class SensorChart {
  /** The range's Readings, ordered by Device time. */
  readonly readings = input.required<Reading[]>();
  readonly sensor = input.required<Sensor>();
  readonly lines = input<RuleLine[]>([]);
  /** The replay time (Device time, ms), drawn as a vertical line. */
  readonly cursor = input<number | null>(null);

  protected readonly sensorNames = sensorNames;
  private readonly host: HTMLElement = inject(ElementRef).nativeElement;
  private chart?: Chart<'line', { x: number; y: number }[]>;

  constructor() {
    const cursor: Plugin<'line'> = {
      id: 'cursor',
      afterDatasetsDraw: ({ ctx, chartArea, scales }) => {
        const time = this.cursor();
        if (time === null) return;
        const x = scales['x'].getPixelForValue(time);
        ctx.save();
        ctx.strokeStyle = colour(this.host, '--accent');
        ctx.lineWidth = 1.5;
        ctx.beginPath();
        ctx.moveTo(x, chartArea.top);
        ctx.lineTo(x, chartArea.bottom);
        ctx.stroke();
        ctx.restore();
      },
    };

    afterNextRender(() => {
      const [grid, muted] = [colour(this.host, '--line'), colour(this.host, '--muted')];
      this.chart = new Chart(this.host.querySelector('canvas')!, {
        type: 'line',
        data: { datasets: [] },
        plugins: [cursor],
        options: {
          animation: false,
          responsive: true,
          maintainAspectRatio: false,
          parsing: false,
          normalized: true,
          // Min-max keeps the peaks that breach a threshold when a long range has more Readings than pixels.
          plugins: { decimation: { enabled: true, algorithm: 'min-max' } },
          scales: {
            x: { type: 'linear', grid: { color: grid }, ticks: { color: muted, maxTicksLimit: 7 } },
            y: { ...axis[this.sensor()], grid: { color: grid }, ticks: { color: muted, maxTicksLimit: 4 } },
          },
        },
      });
      this.draw();
    });
    afterRenderEffect(() => {
      this.readings();
      this.lines();
      this.draw();
    });
    afterRenderEffect(() => {
      this.cursor();
      this.chart?.draw();
    });
    inject(DestroyRef).onDestroy(() => this.chart?.destroy());
  }

  private draw(): void {
    const chart = this.chart;
    if (!chart) return;

    const value = sensorValue[this.sensor()];
    const points = this.readings().flatMap((r) => {
      const y = value(r);
      return y === null ? [] : [{ x: Date.parse(r.deviceTime), y }];
    });
    const [first, last] = [points[0]?.x ?? 0, points.at(-1)?.x ?? 0];
    const across = (y: number) => [{ x: first, y }, { x: last, y }];
    const [accent, alert, muted] = ['--accent', '--alert', '--muted'].map((name) => colour(this.host, name));
    chart.data.datasets = [
      { data: points, borderColor: accent, backgroundColor: `${accent}22`, fill: true, borderWidth: 1.6, pointRadius: 0 },
      ...this.lines().flatMap((line) => [
        { data: across(line.threshold), borderColor: alert, borderDash: [4, 3], borderWidth: 1.2, pointRadius: 0 },
        { data: across(line.clear), borderColor: muted, borderDash: [2, 3], borderWidth: 1.2, pointRadius: 0 },
      ]),
    ];
    const x = chart.options.scales!['x']!;
    x.min = first;
    x.max = last;
    x.ticks!.callback = (time) => tickLabel(Number(time), last - first);
    chart.update('none');
  }
}
